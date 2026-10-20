using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace QRSnip;

/// <summary>A Grid whose pointer cursor can be set from XAML (ProtectedCursor is otherwise inaccessible).</summary>
public sealed partial class CursorGrid : Grid
{
    private InputSystemCursorShape _shape = InputSystemCursorShape.Arrow;

    public InputSystemCursorShape CursorShape
    {
        get => _shape;
        set
        {
            _shape = value;
            ProtectedCursor = InputSystemCursor.Create(value);
        }
    }
}
