using System.IO;
using System.Linq.Expressions;
using System.Text.Json;
using Sudoku.Models;
using Sudoku.Solvers;

namespace Sudoku.Services;

/// <summary>盤面状態をファイルに保存/復元する（1件のみ保持）</summary>
public class SaveDataService
{
    private readonly string _filePath;

    public SaveDataService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Sudoku");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "savedata.json");
    }

    public bool HasSaveData => File.Exists(_filePath);

    public void Save(Difficulty difficulty, GameMode mode, TimeSpan elapsed, Board puzzle, Board solution, List<Cage>? cages)
    {
        var data = new SaveData()
        {
            Difficulty = difficulty,
            Mode = mode,
            ElapsedSeconds = elapsed.TotalSeconds,
        };

        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                var cell = puzzle.GetCell(r, c);
                data.Puzzle.Add(new CellSaveData
                {
                    Row = r,
                    Col = c,
                    Value = cell.Value,
                    IsGiven = cell.IsGiven,
                    Candidates = cell.CandidateMarks.ToList()
                });
                data.Solution.Add(solution.GetCell(r, c).Value!.Value);
            }
        }

        if (cages is not null)
        {
            foreach (var cage in cages)
            {
                data.Cages.Add(new CageSaveData()
                {
                    CellIndexes = cage.Cells.Select(cell => cell.Row * Board.Size + cell.Col).ToList(),
                    TargetSum = cage.TargetSum
                });
            }
        }

        var json = JsonSerializer.Serialize(data);
        File.WriteAllText(_filePath, json);
    }

    public (Board Puzzle, Board Solution, Difficulty Difficulty, GameMode Mode, List<Cage>? Cages, TimeSpan Elapsed)? Load()
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<SaveData>(json);
            if (data is null) return null;

            var puzzle = new Board();
            foreach (var cellData in data.Puzzle)
            {
                if (cellData.IsGiven && cellData.Value.HasValue)
                {
                    puzzle.SetGivenAt(cellData.Row, cellData.Col, cellData.Value.Value);
                }
                else
                {
                    var cell = puzzle.GetCell(cellData.Row, cellData.Col);
                    if (cellData.Value.HasValue)
                        cell.SetValue(cellData.Value.Value);
                    foreach (var candidate in cellData.Candidates)
                        cell.ToggleCandidate(candidate);
                }
            }

            var solution = new Board();
            for (int i = 0; i < data.Solution.Count; i++)
            {
                int row = i / Board.Size;
                int col = i % Board.Size;
                solution.SetGivenAt(row, col, data.Solution[i]);
            }

            List<Cage>? cages = null;
            if (data.Cages.Count > 0)
            {
                cages = data.Cages
                    .Select(cageData => new Cage(
                        cageData.CellIndexes.Select(index => (index / Board.Size, index % Board.Size)).ToList(),
                        cageData.TargetSum))
                    .ToList();
            }

            return (puzzle, solution, data.Difficulty, data.Mode, cages, TimeSpan.FromSeconds(data.ElapsedSeconds));
        }
        catch
        {
            return null; // 壊れた保存データは無視
        }
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }
}