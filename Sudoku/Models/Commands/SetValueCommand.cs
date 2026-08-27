using System.Collections.Generic;
using System.Linq;

namespace Sudoku.Models.Commands;

/// <summary>マスに確定値を設定する操作。
/// あわせて、指定された関連マス（同じ行・列・ブロック）の候補値メモから、
/// 入力した数字と同じものを自動的に取り除く。
/// </summary>
public class SetValueCommand : ICellCommand
{
    private readonly Cell _cell;
    private readonly int _newValue;
    private readonly List<Cell> _relatedCells;

    private int? _previousValue;
    private HashSet<int> _previousCandidates = new();
    private readonly Dictionary<Cell, int> _removedCandidateFrom = new();

    public SetValueCommand(Cell cell, int newValue, IEnumerable<Cell>? relatedCells = null)
    {
        _cell = cell;
        _newValue = newValue;
        _relatedCells = relatedCells?.ToList() ?? new List<Cell>();
    }

    public void Execute()
    {
        _previousValue = _cell.Value;
        _previousCandidates = new HashSet<int>(_cell.CandidateMarks);
        _cell.SetValue(_newValue);

        _removedCandidateFrom.Clear();
        foreach (var related in _relatedCells)
        {
            if (related.HasValue) continue;
            if (!related.CandidateMarks.Contains(_newValue)) continue;

            related.ToggleCandidate(_newValue); // 該当する候補値メモを削除
            _removedCandidateFrom[related] = _newValue;
        }
    }

    public void Undo()
    {
        if (_previousValue.HasValue)
            _cell.SetValue(_previousValue.Value);
        else
            _cell.ClearValue();

        foreach (var digit in _previousCandidates)
            if (!_cell.CandidateMarks.Contains(digit))
                _cell.ToggleCandidate(digit);

        foreach (var (relatedCell, digit) in _removedCandidateFrom)
            if (!relatedCell.CandidateMarks.Contains(digit))
                relatedCell.ToggleCandidate(digit);
    }
}