using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>二分法迭代的一步。</summary>
    public sealed class BalanceIteration
    {
        public BalanceIteration(double offset, double net)
        {
            Offset = offset;
            Net = net;
        }

        public double Offset { get; }
        public double Net { get; }
    }

    /// <summary>平衡求解結果。</summary>
    public sealed class BalanceSolveResult
    {
        public BalanceSolveResult(
            double solvedOffset,
            double achievedNet,
            bool converged,
            IReadOnlyList<BalanceIteration> iterations)
        {
            SolvedOffset = solvedOffset;
            AchievedNet = achievedNet;
            Converged = converged;
            Iterations = iterations;
        }

        public double SolvedOffset { get; }
        public double AchievedNet { get; }
        public bool Converged { get; }
        public IReadOnlyList<BalanceIteration> Iterations { get; }
    }

    /// <summary>
    /// 以二分法求「淨土方(offset) = 目標」的樓板統一升降量。
    /// 前提：淨土方隨 offset 單調遞增（升板→挖少填多）；評估函式由呼叫端注入，
    /// 純邏輯不接觸 Revit，數字永遠來自呼叫端的正式管線。
    /// </summary>
    public static class BalanceSolver
    {
        public static BalanceSolveResult Solve(
            Func<double, double> evaluateNet,
            double target,
            double tolerance,
            double lowerBound,
            double upperBound,
            int maxIterations)
        {
            if (evaluateNet == null) throw new ArgumentNullException(nameof(evaluateNet));
            if (tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance), "容差必須大於 0。");
            if (maxIterations <= 0) throw new ArgumentOutOfRangeException(nameof(maxIterations), "迭代次數必須大於 0。");
            if (lowerBound >= upperBound)
            {
                throw new ArgumentException("下界必須小於上界。");
            }

            var lowerNet = evaluateNet(lowerBound) - target;
            if (Math.Abs(lowerNet) <= tolerance)
            {
                return new BalanceSolveResult(
                    lowerBound, lowerNet + target, true,
                    new[] { new BalanceIteration(lowerBound, lowerNet + target) });
            }

            var upperNet = evaluateNet(upperBound) - target;
            if (Math.Abs(upperNet) <= tolerance)
            {
                return new BalanceSolveResult(
                    upperBound, upperNet + target, true,
                    new[] { new BalanceIteration(upperBound, upperNet + target) });
            }

            if (Math.Sign(lowerNet) == Math.Sign(upperNet))
            {
                throw new InvalidOperationException(
                    $"調整區間無法夾住目標淨土方：offset {lowerBound:F2} 時淨值 {lowerNet + target:F2}、"
                    + $"offset {upperBound:F2} 時淨值 {upperNet + target:F2}，兩端同號。"
                    + "請放寬 maxAdjustMeters 或檢查目標是否可達。");
            }

            var iterations = new List<BalanceIteration>();
            var low = lowerBound;
            var high = upperBound;
            var lowNet = lowerNet;
            var bestOffset = lowerBound;
            var bestNet = lowerNet;
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var middle = (low + high) / 2.0;
                var middleNet = evaluateNet(middle) - target;
                iterations.Add(new BalanceIteration(middle, middleNet + target));
                if (Math.Abs(middleNet) < Math.Abs(bestNet))
                {
                    bestNet = middleNet;
                    bestOffset = middle;
                }

                if (Math.Abs(middleNet) <= tolerance)
                {
                    return new BalanceSolveResult(middle, middleNet + target, true, iterations);
                }

                if (Math.Sign(middleNet) == Math.Sign(lowNet))
                {
                    low = middle;
                    lowNet = middleNet;
                }
                else
                {
                    high = middle;
                }
            }

            return new BalanceSolveResult(bestOffset, bestNet + target, false, iterations);
        }
    }
}
