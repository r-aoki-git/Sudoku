namespace Sudoku.Solvers;

/// <summary>
/// ソルバー内部の詳細ログ出力を制御するスイッチ。
///
/// FortyFiveRuleTechnique・InnieOutieTechnique・KillerPairTripleTechnique は、
/// 候補を1つ絞り込むたびに（＝HumanSolverのメインループが1回進むたびに）Debug.WriteLine
/// を呼んでいたため、1回の生成試行だけで数十〜数百回のログ出力が発生していた。
///
/// Debug.WriteLineは内部で共有のトレースリスナーに対して排他ロックを取るため、
/// ParallelKillerSudokuGeneratorで複数ワーカーを並列実行すると、このロックの奪い合いで
/// 本来並列に走るはずのCPU計算が実質的に直列化されてしまう問題があった。
///
/// 既定ではfalse（出力しない）にしておき、個々のテクニックの動作を手動で追いたい時だけ
/// trueに切り替える。
/// </summary>
public static class SolverDiagnostics
{
    public static bool VerboseLogging { get; set; } = false;
}