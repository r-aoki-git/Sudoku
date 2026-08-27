using Sudoku.Models;

namespace Sudoku.Solvers;

/// <summary>
/// 人間の解法テクニック1つを表すインターフェース。
/// </summary>
public interface ISolvingTechnique
{
    /// <summary>要件定義書 5.8 のテクニックレベル（1〜4）。</summary>
    int Level { get; }

    /// <summary>true: 成功時にマスへ数字を確定する。false: 候補の絞り込みのみ行う。</summary>
    bool PlacesValue { get; }

    /// <summary>テクニック名（デバッグ表示用）。</summary>
    string Name { get; }

    /// <summary>
    /// このテクニックを1回分適用する。適用できてマスが1つ確定したら true を返す。
    /// （呼び出し側は true が返るたびに candidates を再計算して呼び直す想定）
    /// </summary>
    bool TryApply(Board board, CandidateGrid candidates);
}