using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WinNewsReader.Core.ArticleRendering;

namespace WinNewsReader.App;

/// <summary>
/// Reader View's second try. A plain download is refused by sites that check for a bot (403, a "Just a moment…" page) and
/// gets only an empty shell from sites that build the page with scripts. This loads the link in a WebView2 the reader never
/// sees, the same Edge engine the article pane uses, waits for the page to render, and hands back its HTML.
/// One hidden browser is kept for the life of the window, because starting one takes seconds; <see cref="WarmUpAsync"/>
/// starts it ahead of time. It loads one page at a time. Call it on the UI thread.
/// </summary>
public sealed class HiddenBrowserFetcher(CoreWebView2Environment environment, nint windowHandle)
{
	private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

	/// <summary>How long a bot check, which solves itself in a real browser, gets to finish.</summary>
	private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(12);

	/// <summary>A pause after the page loads for the scripts that build the article.</summary>
	private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

	private static readonly string[] ChallengeTitles = ["just a moment", "attention required", "checking your browser", "verify you are human", "one moment, please"];

	private readonly SemaphoreSlim _oneAtATime = new(1, 1);
	private CoreWebView2Controller? _controller;

	/// <summary>Starts the hidden browser now, so the first page that needs it doesn't wait for it. Failures are left for the first real use.</summary>
	public async Task WarmUpAsync()
	{
		try
		{
			await _oneAtATime.WaitAsync();
			try
			{
				await ControllerAsync(null);
			}
			finally
			{
				_oneAtATime.Release();
			}
		}
		catch (Exception)
		{
			// Tried again, and reported, when Reader View needs it.
		}
	}

	public async Task<RenderedPage> FetchAsync(string url, CancellationToken cancellationToken, IProgress<string>? progress)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(LoadTimeout);
		await _oneAtATime.WaitAsync(timeout.Token);
		CoreWebView2? web = null;
		try
		{
			var controller = await ControllerAsync(progress);
			timeout.Token.ThrowIfCancellationRequested();
			web = controller.CoreWebView2;
			progress?.Report("Loading the page…");
			var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
			web.NavigationCompleted += OnCompleted;
			try
			{
				web.Navigate(url);
				if (!await loaded.Task.WaitAsync(timeout.Token))
				{
					throw new InvalidOperationException("The page didn’t load.");
				}
			}
			finally
			{
				web.NavigationCompleted -= OnCompleted;
			}

			var challengeEnds = DateTime.UtcNow + ChallengeTimeout;
			while (IsChallenge(web.DocumentTitle) && DateTime.UtcNow < challengeEnds)
			{
				await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
			}
			await Task.Delay(SettleTime, timeout.Token);

			var html = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.documentElement.outerHTML"));
			return new RenderedPage(web.Source, html ?? "");
		}
		finally
		{
			if (web is not null)
			{
				// Stops a page that was cancelled or timed out, and lets go of the one just read.
				try
				{
					web.Stop();
					web.Navigate("about:blank");
				}
				catch (Exception)
				{
					// The window closed while a page was loading.
				}
			}
			_oneAtATime.Release();
		}
	}

	/// <summary>The hidden browser, started on first use. Callers hold <see cref="_oneAtATime"/>.</summary>
	private async Task<CoreWebView2Controller> ControllerAsync(IProgress<string>? progress)
	{
		if (_controller is { } existing)
		{
			return existing;
		}
		progress?.Report("Starting the browser…");
		var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)windowHandle));
		controller.IsVisible = false;
		controller.Bounds = new Windows.Foundation.Rect(0, 0, 1280, 900);
		var web = controller.CoreWebView2;
		web.IsMuted = true;
		// The page is read, never used: no windows, downloads, other apps' links or permission prompts.
		web.NewWindowRequested += (_, args) => args.Handled = true;
		web.DownloadStarting += (_, args) => args.Cancel = true;
		web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
		web.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
		_controller = controller;
		return controller;
	}

	private static bool IsChallenge(string? title) =>
		title is { Length: > 0 } && ChallengeTitles.Any(challenge => title.Contains(challenge, StringComparison.OrdinalIgnoreCase));
}
