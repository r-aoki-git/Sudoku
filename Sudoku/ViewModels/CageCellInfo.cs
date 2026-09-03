namespace Sudoku.ViewModels;

///<summary>1マス分のケージ描画情報。キラーナンプレでのみ使用。
///ケージの枠線は盤面全体で1本のGeometry（BoardViewModel.CageOutline）として描くため、
///ここではマス単位の情報である合計値ラベルだけを持つ。</summary>
public sealed class CageCellInfo
{
    ///<summary>ケージ合計値のラベル文字列（ケージの左上マスにのみ非空の値が入る）</summary>
    public string SumText { get; init; } = "";
}
