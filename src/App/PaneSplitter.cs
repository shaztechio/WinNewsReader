using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinNewsReader.App;

/// <summary>A vertical bar that resizes <see cref="Column"/>, the grid column to its left, when dragged.</summary>
public sealed partial class PaneSplitter : Grid
{
	private bool _dragging;
	private double _startX;
	private double _startWidth;

	public PaneSplitter()
	{
		// A transparent background makes the whole bar hit-testable.
		Background = new SolidColorBrush(Colors.Transparent);
		ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
		PointerPressed += OnPointerPressed;
		PointerMoved += OnPointerMoved;
		PointerReleased += OnPointerReleased;
		PointerCaptureLost += (_, _) => _dragging = false;
	}

	public ColumnDefinition? Column { get; set; }

	public double MinimumWidth { get; set; } = 160;

	public double MaximumWidth { get; set; } = 640;

	private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (Column is null)
		{
			return;
		}
		_dragging = CapturePointer(e.Pointer);
		_startX = e.GetCurrentPoint(null).Position.X;
		_startWidth = Column.ActualWidth;
		e.Handled = true;
	}

	private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_dragging && Column is not null)
		{
			var width = _startWidth + e.GetCurrentPoint(null).Position.X - _startX;
			Column.Width = new Microsoft.UI.Xaml.GridLength(Math.Clamp(width, MinimumWidth, MaximumWidth));
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
