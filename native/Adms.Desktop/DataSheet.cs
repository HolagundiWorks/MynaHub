using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class DataSheet : UserControl
{
    private readonly string[] _headers;
    private readonly List<FrameworkElement[]> _cells;

    public DataSheet(string name, string[] headers, List<FrameworkElement[]> cells, UIElement content)
    {
        _headers = headers;
        _cells = cells;
        AutomationProperties.SetName(this, name);
        Content = content;
        Flow.Surface(this);
    }

    public int RowCount => _cells.Count;

    public int ColumnCount => _headers.Length;

    public FrameworkElement Cell(int row, int column) => _cells[row][column];

    protected override AutomationPeer OnCreateAutomationPeer() => new DataSheetPeer(this);
}

internal sealed class DataSheetPeer : FrameworkElementAutomationPeer, ITableProvider, IGridProvider
{
    private readonly DataSheet _sheet;
    private readonly Dictionary<(int Row, int Column), DataCellPeer> _peers = new();

    public DataSheetPeer(DataSheet owner) : base(owner) => _sheet = owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Table;

    protected override string GetClassNameCore() => "DataSheet";

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Table or PatternInterface.Grid ? this : base.GetPatternCore(patternInterface);

    public int RowCount => _sheet.RowCount;

    public int ColumnCount => _sheet.ColumnCount;

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    public IRawElementProviderSimple GetItem(int row, int column) => ProviderFromPeer(Peer(row, column));

    public IRawElementProviderSimple[] GetColumnHeaders()
    {
        var headers = new IRawElementProviderSimple[ColumnCount];
        for (var column = 0; column < ColumnCount; column++)
            headers[column] = ProviderFromPeer(Peer(0, column));
        return headers;
    }

    public IRawElementProviderSimple[] GetRowHeaders() => [];

    internal DataCellPeer Peer(int row, int column)
    {
        if (!_peers.TryGetValue((row, column), out var peer))
        {
            peer = new DataCellPeer(_sheet.Cell(row, column), this, row, column);
            _peers[(row, column)] = peer;
        }
        return peer;
    }
}

internal sealed class DataCellPeer : FrameworkElementAutomationPeer, ITableItemProvider, IGridItemProvider
{
    private readonly DataSheetPeer _table;

    public DataCellPeer(FrameworkElement owner, DataSheetPeer table, int row, int column) : base(owner)
    {
        _table = table;
        Row = row;
        Column = column;
    }

    public int Column { get; }

    public int ColumnSpan => 1;

    public int Row { get; }

    public int RowSpan => 1;

    public IRawElementProviderSimple ContainingGrid => ProviderFromPeer(_table);

    public IRawElementProviderSimple[] GetColumnHeaderItems() =>
        Row == 0 ? [] : [ProviderFromPeer(_table.Peer(0, Column))];

    public IRawElementProviderSimple[] GetRowHeaderItems() => [];

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        Row == 0 ? AutomationControlType.HeaderItem : AutomationControlType.DataItem;

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.TableItem or PatternInterface.GridItem ? this : base.GetPatternCore(patternInterface);
}
