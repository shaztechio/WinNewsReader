using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinNewsReader.App;

/// <summary>A horizontal bar that resizes <see cref="Row"/>, the grid row above it, when dragged.</summary>
public sealed partial class RowSplitter : Grid
{
	private bool _dragging;
	private double _startY;
	private double _startHeight;

	public RowSplitter()
	{
		// A transparent background makes the whole bar hit-testable.
		Background = new SolidColorBrush(Colors.Transparent);
		ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
		PointerPressed += OnPointerPressed;
		PointerMoved += OnPointerMoved;
		PointerReleased += OnPointerReleased;
		PointerCaptureLost += (_, _) => _dragging = false;
	}

	public RowDefinition? Row { get; set; }

	public double MinimumHeight { get; set; } = 120;

	/// <summary>How much of the grid the row above may take at most, leaving the rest for the pane below.</summary>
	public double MaximumHeight { get; set; } = 2000;

	private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (Row is null)
		{
			return;
		}
		_dragging = CapturePointer(e.Pointer);
		_startY = e.GetCurrentPoint(null).Position.Y;
		_startHeight = Row.ActualHeight;
		e.Handled = true;
	}

	private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_dragging && Row is not null)
		{
			var height = _startHeight + e.GetCurrentPoint(null).Position.Y - _startY;
			Row.Height = new GridLength(Math.Clamp(height, MinimumHeight, MaximumHeight));
			e.Handled = true;
		}
	}

	private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
	{
		_dragging = false;
		ReleasePointerCapture(e.Pointer);
		e.Handled = true;
	}
}
