using Sudoku.Models;

namespace Sudoku.Solvers.Techniques;

/// <summary>
/// レベル3（キラーモード向け）：Locked Candidates
/// アルゴリズム自体は通常モードと同じだが、キラーモードでは難易度レベルの位置付けが
/// 異なるため、レベル値だけを3に差し替えるラッパー。
/// </summary>
public class KillerLockedCandidatesTechnique : ISolvingTechnique
{
    private readonly LockedCandidatesTechnique _inner = new();

    public int Level => 3;
    public string Name => _inner.Name;
    public bool PlacesValue => _inner.PlacesValue;

    public bool TryApply(Board board, CandidateGrid candidates) => _inner.TryApply(board, candidates);
}