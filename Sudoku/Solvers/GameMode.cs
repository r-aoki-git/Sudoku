namespace Sudoku.Solvers;

///<summary>ゲームモード（通常のナンプレ / キラーナンプレ）</summary>
public enum  GameMode
{
    ///<summary>通常のナンプレ（初期配置あり）</summary>
    Classic,

    ///<summary>キラーナンプレ（初期配置なし、ケージ合計のみが手がかり）</summary>
    Killer
}