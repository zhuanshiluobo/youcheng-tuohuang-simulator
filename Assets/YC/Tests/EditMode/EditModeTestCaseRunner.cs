using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace YC.Tests.EditMode
{
    internal static class EditModeTestCaseRunner
    {
        public static void Run<T>(
            IEnumerable<T> cases,
            Action<T> assertion,
            Func<T, string> describe)
        {
            var failures = new List<string>();
            foreach (var testCase in cases)
            {
                try
                {
                    assertion(testCase);
                }
                catch (AssertionException exception)
                {
                    failures.Add(describe(testCase) + ": " + exception.Message);
                }
            }

            if (failures.Count > 0)
            {
                Assert.Fail(string.Join(Environment.NewLine, failures));
            }
        }
    }
}
