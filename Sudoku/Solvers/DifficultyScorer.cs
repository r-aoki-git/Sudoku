namespace Sudoku.Solvers;

public enum Difficulty { Easy, Normal, Hard, Expert, Master }

public enum DifficultySolveStatus
{
    Solved,
    Stuck,
    Timeout,
    Invalid
}

public record DifficultyResult(
    Difficulty Label,
    int Score,
    DifficultySolveStatus Status,
    int MaxLevel,
    int Remaining,
    bool UsedFallback);

/// <summary>
/// HumanSolverの結果からスコアと難易度ラベルを算出する。
///
/// Levelだけではなく、キラー固有テクニックの使用回数も評価する
/// また、フォールバック使用を即Masterにはせず、「人間解法では未完了」として扱う
/// </summary>
public class DifficultyScorer
{
    private static readonly Dictionary<int, int> LevelBasePoints = new()
    {
        [1] = 0,
        [2] = 20,
        [3] = 50,
        [4] = 100,
    };

    private static readonly Dictionary<string, int> TechniquePoints = new()
    {
        // Level 1
        ["Naked Single"] = 0,
        ["Hidden Single"] = 1,

        // キラー固有
        ["Cage Forced Combination"] = 0,
        ["45 Rule (Single Unit)"] = 8,

        // Level 3
        ["Locked Candidates"] = 4,
        ["Killer Locked Candidates"] = 8,
        ["Innie / Outie"] = 10,
        ["Killer Pair / Triple"] = 12,

        // Subsets
        ["Naked Pair"] = 4,
        ["Naked Triple"] = 8,
        ["Hidden Pair"] = 6,
        ["Hidden Triple"] = 12,

        // Level 4
        ["X-Wing"] = 20,
        ["Swordfish"] = 35,
    };

    public DifficultyResult Evaluate(HumanSolveResult result)
    {
        int score = result.MaxLevelUsed switch
        {
            <= 1 => 0,
            2 => LevelBasePoints[2],
            3 => LevelBasePoints[3],
            _ => LevelBasePoints[4],
        };

        foreach (var (name, count) in result.TechniqueUsageByName)
        {
            if (count <= 0)
                continue;

            if (TechniquePoints.TryGetValue(name, out int points))
                score += count * points;
        }

        // 人間解法で最後まで解けなかった場合。
        // これはMaster確定ではなく「Stuck」とする
        if (result.RequiredFallback)
        {
            return new DifficultyResult(
                Difficulty.Master,
                score,
                DifficultySolveStatus.Stuck,
                result.MaxLevelUsed,
                result.RemainingCells,
                true);
        }

        var label = GetDifficultyLabel(score, result.MaxLevelUsed);

        System.Diagnostics.Debug.WriteLine(
            $"[DifficultyScorer] " +
            $"MaxLevel={result.MaxLevelUsed}, " +
            $"Score={score}, " +
            $"Label={label}, " +
            $"Fallback={result.RequiredFallback}");

        return new DifficultyResult(label, score, DifficultySolveStatus.Solved, result.MaxLevelUsed, result.RemainingCells, false);
    }

    private static Difficulty GetDifficultyLabel(int score, int maxLevel)
    {
        if (maxLevel >= 4)
            return Difficulty.Master;

        return score switch
        {
            < 30 => Difficulty.Easy,
            < 80 => Difficulty.Normal,
            < 400 => Difficulty.Hard,
            _ => Difficulty.Expert
        };
    }
}