using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MkPFS.Repair;

namespace MkPFS.Gui.Controls;

/// <summary>Worst state among the blocks of a cell; higher wins.</summary>
public enum BlockState : byte
{
    /// <summary>Stored raw.</summary>
    Raw,

    /// <summary>zlib stream the PS5 decodes correctly.</summary>
    Zlib,

    /// <summary>zlib stream the PS5 may decode wrongly.</summary>
    Risky,

    /// <summary>Does not decode.</summary>
    Undecodable,
}

/// <summary>A run of blocks shown as one cell.</summary>
/// <param name="First">First block.</param>
/// <param name="Last">Last block, inclusive.</param>
public readonly record struct BlockRange(long First, long Last);

/// <summary>
/// PFSC block map: one square per block, or per run of blocks when the image has more blocks than fit in
/// <see cref="MaxRows"/> rows. Drawn directly (no child controls), so 100k+ blocks stay cheap.
/// </summary>
public sealed class BlockMap : Control
{
    /// <summary>Scanned image, or <see langword="null"/> for an empty map.</summary>
    public static readonly StyledProperty<PFSCBlockMap?> MapProperty = AvaloniaProperty.Register<BlockMap, PFSCBlockMap?>(nameof(Map));

    /// <summary>Blocks drawn per cell (1 when every block has its own cell).</summary>
    public static readonly DirectProperty<BlockMap, long> BlocksPerCellProperty =
        AvaloniaProperty.RegisterDirect<BlockMap, long>(nameof(BlocksPerCell), map => map.BlocksPerCell);

    /// <summary>Square size in pixels, gap included.</summary>
    public const double Pitch = 9;

    /// <summary>Most rows before blocks are grouped into cells.</summary>
    public const int MaxRows = 24;

    private static readonly IBrush[] Brushes =
    [
        new ImmutableSolidColorBrush(Color.Parse("#2B3553")),
        new ImmutableSolidColorBrush(Color.Parse("#00C8FF")),
        new ImmutableSolidColorBrush(Color.Parse("#FF3B5C")),
        new ImmutableSolidColorBrush(Color.Parse("#FFB800")),
    ];

    private static readonly IPen SelectionPen = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 1.5);

    private BlockState[] _cells = [];
    private long _blocksPerCell = 1;
    private int _columns = 1;
    private int _selected = -1;

    /// <summary>Raised when the user clicks a cell.</summary>
    public event EventHandler<BlockRange>? CellSelected;

    /// <summary>Scanned image.</summary>
    public PFSCBlockMap? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    /// <summary>Blocks drawn per cell.</summary>
    public long BlocksPerCell
    {
        get => _blocksPerCell;
        private set => SetAndRaise(BlocksPerCellProperty, ref _blocksPerCell, value);
    }

    /// <summary>State a block is drawn with.</summary>
    /// <param name="map">Scanned image.</param>
    /// <param name="index">Block index.</param>
    /// <returns>State.</returns>
    public static BlockState StateOf(PFSCBlockMap map, long index) =>
        map.Undecodable[index] ? BlockState.Undecodable
        : map.Risky[index] ? BlockState.Risky
        : map.IsRaw(index) ? BlockState.Raw
        : BlockState.Zlib;

    /// <summary>Blocks covered by cell <paramref name="cell"/>.</summary>
    /// <param name="cell">Cell index.</param>
    /// <returns>Range, clamped to the last block.</returns>
    public BlockRange RangeOf(int cell)
    {
        long first = cell * BlocksPerCell;
        return new BlockRange(first, Math.Min(first + BlocksPerCell, Map?.BlockCount ?? 0) - 1);
    }

    /// <summary>Select the cell at <paramref name="point"/> and raise <see cref="CellSelected"/>.</summary>
    /// <param name="point">Position in control coordinates.</param>
    /// <returns><see langword="true"/> when a cell is there.</returns>
    public bool SelectAt(Point point)
    {
        int column = (int)(point.X / Pitch);
        int row = (int)(point.Y / Pitch);
        int cell = (row * _columns) + column;
        if (point.X < 0 || point.Y < 0 || column >= _columns || cell >= _cells.Length)
        {
            return false;
        }

        _selected = cell;
        InvalidateVisual();
        CellSelected?.Invoke(this, RangeOf(cell));
        return true;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double size = Pitch - 2;
        for (int cell = 0; cell < _cells.Length; cell++)
        {
            Rect rect = new((cell % _columns) * Pitch, (cell / _columns) * Pitch, size, size);
            context.FillRectangle(Brushes[(int)_cells[cell]], rect, 1.5f);
        }

        if (_selected >= 0 && _selected < _cells.Length)
        {
            context.DrawRectangle(null, SelectionPen, new Rect((_selected % _columns) * Pitch, (_selected / _columns) * Pitch, size, size).Inflate(1), 2, 2);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        Layout(Math.Max(1, (int)(width / Pitch)));
        int rows = (_cells.Length + _columns - 1) / _columns;
        return new Size(width, rows * Pitch);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MapProperty)
        {
            _selected = -1;
            _columns = 0; // force a new layout
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (SelectAt(e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    // Group blocks into cells so the map fits in MaxRows rows, then keep the worst state of each cell.
    private void Layout(int columns)
    {
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        PFSCBlockMap? map = Map;
        if (map is null || map.BlockCount == 0)
        {
            _cells = [];
            BlocksPerCell = 1;
            return;
        }

        long capacity = (long)columns * MaxRows;
        long perCell = Math.Max(1, (map.BlockCount + capacity - 1) / capacity);
        BlockState[] cells = new BlockState[(int)((map.BlockCount + perCell - 1) / perCell)];
        for (long block = 0; block < map.BlockCount; block++)
        {
            int cell = (int)(block / perCell);
            BlockState state = StateOf(map, block);
            if (state > cells[cell])
            {
                cells[cell] = state;
            }
        }

        _cells = cells;
        _selected = -1;
        BlocksPerCell = perCell;
        InvalidateVisual();
    }
}
