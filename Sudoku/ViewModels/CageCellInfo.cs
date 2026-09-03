namespace Sudoku.ViewModels;

///<summary>1マス分のケージ描画情報（境界線・合計値ラベル）。キラーナンプレでのみ使用。</summary>
public sealed class CageCellInfo
{
    ///<summary>上辺がケージの境界かどうか</summary>
    public bool BorderTop { get; init; }

    ///<summary>下辺がケージの境界かどうか</summary>
    public bool BorderBottom { get; init; }

    ///<summary>左辺がケージの境界かどうか</summary>
    public bool BorderLeft { get; init; }

    ///<summary>右辺がケージの境界かどうか</summary>
    public bool BorderRight { get; init; }

    ///<summary>ケージ合計値のラベル文字列（ケージの左上マスにのみ非空の値が入る）</summary>
    public string SumText { get; init; } = "";
}