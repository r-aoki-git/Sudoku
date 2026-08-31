# キラーナンプレ盤面生成ロジックの抜本的修正

## 問題の分析

テストログから、**Hard盤面の生成が5回中0回成功**している原因が明確に特定できました。

### 根本原因：CageGeneratorが大量の単セルケージを生成している

ログの全行が同じパターンを示しています：

```
[CageGenerator] Size1=53, Size2=11, Size3=8, Size4=6, ...
[DifficultyCheck] Requested=Hard, Actual=Easy, Score=20, MaxLv=1
```

**81セル中53個が単セル（Size1）** → ケージの制約情報が少ないため、Cage Forced Combination だけで全マス確定 → **常にEasy判定** → Hard判定に一致せず全試行が棄却。

### なぜ単セルが大量発生するのか

[CageGenerator.cs](file:///c:/Users/r-aoki/Documents/MyProjects/Sudoku/Sudoku/Generators/CageGenerator.cs) の問題点：

1. **`GetMaxTargetSize()` が過度に制限的** (L270-288)
   - 残り30セル以下で最大サイズ5、残り18セル以下で最大サイズ3、残り8セル以下で最大サイズ3
   - 盤面の中盤〜終盤でケージサイズが制限されすぎ、配置不能セルが大量発生

2. **Hardの `SizeWeights` がサイズ2〜5中心** (L38) だが、`GetMaxTargetSize` による上限のせいで実質サイズ2-3しか試行されない段階が早く訪れる

3. **TryPartition の貪欲アルゴリズム** がバックトラックなし。ケージ配置が詰まるとすべて単セルにフォールバック

4. **`IsCageStructureAcceptable` フィルタ** (KillerSudokuGenerator L247-279)：`Hard` では `singles <= 7 && nonSingles >= 17` を要求するが、CageGeneratorが常に40+個のシングルを生成するため、このフィルタで**全ケージ構造が棄却**される → 試行の無駄

### 一般的なキラーナンプレ生成アルゴリズム

一般的なキラーナンプレの盤面生成では：

1. **完成盤面を先に生成**（通常のナンプレ完成盤面）
2. **ランダムウォーク＋Union-Findでケージ分割** — 隣接セルをランダムに統合していく方式が最も安定
3. **唯一解検証**（制約伝播＋必要に応じてバックトラック）

現在のコードは Step 2 が「Seed→DFS拡張」方式で、これ自体は問題ないのですが、盤面全体の分割を1パスで行おうとしてバックトラックしないため、中盤以降で詰まる構造になっています。

---

## 提案する修正方針

CageGeneratorを「ランダムなグリッドパーティション」方式に全面書き換えます。

### アルゴリズム概要

```
1. 全81セルのグリッドを用意。各セルを自分だけのケージとして初期化
2. 全ての隣接セルペア（辺を共有する）のリストをシャッフル
3. ペアリストを順に見ていき、2つのケージを統合:
   - 統合後のサイズが上限を超えない
   - 統合後のケージ内に同じ数字が存在しない
   - 統合した場合にのみマージ
4. 難易度に応じてケージサイズの目標分布を制御（マージ回数・上限サイズ）
5. 生成されたケージ構造を検証（構造フィルタ + 唯一解検証 + 難易度判定）
```

### この方式の利点

- **バックトラック不要**: 全セルが最初からケージに所属。マージできないペアはスキップするだけ
- **デッドロックなし**: マージに失敗しても元のシングルケージがそのまま残るだけ
- **サイズ分布の制御が容易**: マージ回数や目標サイズ範囲で自然に分布を調整
- **高速**: O(n)でグリッド全体のパーティションが完了（nは隣接ペア数≈144）
- **一般的**: 多くのキラーナンプレ生成器で採用されているアルゴリズム

---

## Proposed Changes

### CageGenerator

#### [MODIFY] [CageGenerator.cs](file:///c:/Users/r-aoki/Documents/MyProjects/Sudoku/Sudoku/Generators/CageGenerator.cs)

**全面書き換え**。ランダムウォーク＋Union-Find方式に変更：

- Union-Findデータ構造でケージの統合を管理
- 全隣接ペアをシャッフルして順に統合を試みる「ランダムマージ」方式
- 難易度ごとのケージサイズ上限・目標平均サイズ設定
  - Easy: 上限4, 目標平均1.8 (シングル多め)
  - Normal: 上限5, 目標平均2.2
  - Hard: 上限6, 目標平均2.8-3.5 (2~5セルケージが中心)
  - Expert: 上限7, 目標平均3.5
  - Master: 上限8, 目標平均4.0
- 複数回の分割試行（高速なため多数可能）
- 構造チェック：目標の平均サイズ・シングル数範囲に収まったらケージ確定

---

### KillerSudokuGenerator

#### [MODIFY] [KillerSudokuGenerator.cs](file:///c:/Users/r-aoki/Documents/MyProjects/Sudoku/Sudoku/Generators/KillerSudokuGenerator.cs)

- ケージ生成とフィルタリングの修正：
  - `IsCageStructureAcceptable` の閾値を新しいケージ生成方式に合わせて調整
  - タイムバジェット配分の最適化（ケージ生成が高速になるため、より多くの試行が可能）
  - ケージ構造の生成フェーズで不要な再試行を減らす

---

## Verification Plan

### Automated Tests

テストは手動でビルドし、既存テスト（Hard盤面5回生成）を実行して成功率を検証：

```bash
dotnet build
```

その後、デバッグ実行でHard盤面生成のログを確認し：
- 生成成功率が5/5に近いこと
- 生成時間が1回あたり10秒以内であること
- ケージ構造が妥当であること（Size1が7以下、nonSinglesが17以上）

### Manual Verification

- 各難易度（Easy/Normal/Hard/Expert）で盤面を生成し、ケージ分布とDifficulty判定を確認
