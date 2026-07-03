using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class BalanceSolverTests
    {
        [Test]
        public void Solve_線性函數_收斂至解()
        {
            // net(x) = 2x − 5，目標 0 → 解 2.5。
            var result = BalanceSolver.Solve(
                evaluateNet: x => (2 * x) - 5,
                target: 0.0,
                tolerance: 1e-6,
                lowerBound: -10.0,
                upperBound: 10.0,
                maxIterations: 60);

            Assert.IsTrue(result.Converged);
            Assert.AreEqual(2.5, result.SolvedOffset, 1e-5);
            Assert.LessOrEqual(System.Math.Abs(result.AchievedNet), 1e-6 + 1e-9);
            Assert.Greater(result.Iterations.Count, 0);
        }

        [Test]
        public void Solve_容差內即停()
        {
            var calls = 0;
            var result = BalanceSolver.Solve(
                x => { calls++; return x; },
                target: 0.0,
                tolerance: 0.5,
                lowerBound: -1.0,
                upperBound: 1.0,
                maxIterations: 60);

            Assert.IsTrue(result.Converged);
            // 中點 0 第一次就落在容差內。
            Assert.AreEqual(1, result.Iterations.Count);
            Assert.AreEqual(3, calls); // 兩端點 + 一次中點
        }

        [Test]
        public void Solve_迭代上限未收斂_Converged為假()
        {
            // 根 = 1/3，二分中點（0、5、2.5…）永遠不會精確命中。
            var result = BalanceSolver.Solve(
                x => (3 * x) - 1,
                target: 0.0,
                tolerance: 1e-12,
                lowerBound: -10.0,
                upperBound: 10.0,
                maxIterations: 3);

            Assert.IsFalse(result.Converged);
            Assert.AreEqual(3, result.Iterations.Count);
        }

        [Test]
        public void Solve_端點同號_無法夾住_拋錯()
        {
            var error = Assert.Throws<System.InvalidOperationException>(
                () => BalanceSolver.Solve(
                    x => x + 100, // 在 [-10,10] 全為正
                    target: 0.0,
                    tolerance: 1.0,
                    lowerBound: -10.0,
                    upperBound: 10.0,
                    maxIterations: 10));
            StringAssert.Contains("無法夾住", error.Message);
        }

        [Test]
        public void Solve_端點本身在容差內_直接回傳()
        {
            var result = BalanceSolver.Solve(
                x => x - 10,
                target: 0.0,
                tolerance: 0.5,
                lowerBound: -10.0,
                upperBound: 10.0,
                maxIterations: 10);

            Assert.IsTrue(result.Converged);
            Assert.AreEqual(10.0, result.SolvedOffset, 1e-9);
        }
    }
}
