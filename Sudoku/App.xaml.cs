using Sudoku.Diagnostics;
using Sudoku.Solvers;
using System.Windows;

namespace Sudoku
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ------------------------------------------------------------
            // キラーナンプレ生成ベンチマーク
            //
            // Easy / Normal / Hard / Expert / Master を
            // 各10問ずつ生成して以下を検証する。
            //
            // 1. 完成盤面が合法
            // 2. ケージ構造が合法
            // 3. 唯一解
            // 4. 要求難易度と実測難易度
            // 5. 生成時間
            // ------------------------------------------------------------

            SolverDiagnostics.VerboseLogging = true;

            KillerSudokuGenerationBenchmark.Run(
                samplesPerDifficulty: 10,
                workers: 4,
                overallTimeoutMs: 10000,
                perAttemptBudgetMs: 2500);
        }
    }
}