using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace XunitReadableTheories
{

    public class Test
    {
        public interface IReadableTestCase
        {
            string TestCase();
        }

        public class TestData 
        {
            public string Description { get; set; }
            public int Expected { get; set; }
            public int X { get; set; }
            public int Y { get; set; }

            //public string TestCase()
            //{
            //    return $"When adding '{X}' and '{Y}' the result should be '{Expected}'";
            //}

            public override string ToString()
            {
                return $"When adding '{X}' and '{Y}' the result should be '{Expected}'";
            }
        }

        public static IEnumerable<object[]> GetTestData(int x, int y, int expected)
        {
            yield return new object[] { new TestData { Description = "Blabla", X = x, Y = y, Expected = expected } };
        }

        //[ReadableTheory(DisplayName = "When adding '{0}' and '{1}' the result should be '{2}'")]
        //[MemberData(nameof(GetTestData), 1, 1, 2)]
        //public void Should(TestData data)
        //{
        //    var i = data.X + data.Y;
        //    i.ShouldBe(data.Expected);
        //}

        [Theory]
        [MemberData(nameof(GetTestData), 1, 1, 2)]
        public void Should1(TestData test)
        {
            var i = test.X + test.Y;
            i.ShouldBe(test.Expected);
        }
    }

    [XunitTestCaseDiscoverer("XunitReadableTheories.ReadableTheoryDiscoverer", "XunitReadableTheories")]
    [AttributeUsage(AttributeTargets.Method)]
    public class ReadableTheory : TheoryAttribute
    {

    }

    public class ReadableTheoryDiscoverer : TheoryDiscoverer
    {
        private readonly IMessageSink _diagnosticMessageSink;

        public ReadableTheoryDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
        {
            _diagnosticMessageSink = diagnosticMessageSink;
        }

        protected override IXunitTestCase CreateTestCaseForTheory(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
            IAttributeInfo theoryAttribute)
        {
            return new ReadableTheoryTestCase(_diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod);
        }
    }

    public class ReadableTheoryTestCase : XunitTheoryTestCase
    {
        public ReadableTheoryTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, ITestMethod testMethod) : base(diagnosticMessageSink, defaultMethodDisplay, testMethod)
        {
        }

        public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments,
            ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        {
            return new ReadableTheoryTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, diagnosticMessageSink, messageBus, aggregator, cancellationTokenSource).RunAsync();
        }
    }

    public class ReadableTheoryTestCaseRunner : XunitTheoryTestCaseRunner
    {
        private readonly IMessageSink _diagnosticMessageSink;
        readonly ExceptionAggregator _cleanupAggregator = new ExceptionAggregator();
        Exception _dataDiscoveryException;
        readonly List<XunitTestRunner> _testRunners = new List<XunitTestRunner>();
        readonly List<IDisposable> _toDispose = new List<IDisposable>();

        public ReadableTheoryTestCaseRunner(IXunitTestCase testCase, string displayName, string skipReason, object[] constructorArguments, IMessageSink diagnosticMessageSink, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) : base(testCase, displayName, skipReason, constructorArguments, diagnosticMessageSink, messageBus, aggregator, cancellationTokenSource)
        {
            _diagnosticMessageSink = diagnosticMessageSink;
        }

        protected override async Task AfterTestCaseStartingAsync()
        {
            await base.AfterTestCaseStartingAsync();

            try
            {
                var dataAttributes = TestCase.TestMethod.Method.GetCustomAttributes(typeof(DataAttribute));

                foreach (var dataAttribute in dataAttributes)
                {
                    var discovererAttribute = dataAttribute.GetCustomAttributes(typeof(DataDiscovererAttribute)).First();
                    var args = discovererAttribute.GetConstructorArguments().Cast<string>().ToList();
                    var discovererType = SerializationHelper.GetType(args[1], args[0]);
                    var discoverer = ExtensibilityPointFactory.GetDataDiscoverer(_diagnosticMessageSink, discovererType);

                    foreach (var dataRow in discoverer.GetData(dataAttribute, TestCase.TestMethod.Method))
                    {
                        _toDispose.AddRange(dataRow.OfType<IDisposable>());

                        ITypeInfo[] resolvedTypes = null;
                        var methodToRun = TestMethod;

                        if (methodToRun.IsGenericMethodDefinition)
                        {
                            resolvedTypes = TestCase.TestMethod.Method.ResolveGenericTypes(dataRow);
                            methodToRun = methodToRun.MakeGenericMethod(resolvedTypes.Select(t => ((IReflectionTypeInfo)t).Type).ToArray());
                        }

                        var parameterTypes = methodToRun.GetParameters().Select(p => p.ParameterType).ToArray();
                        var convertedDataRow = Reflector.ConvertArguments(dataRow, parameterTypes);

                        string theoryDisplayName;
                        if (convertedDataRow.Length == 1 && convertedDataRow[0] is Test.IReadableTestCase)
                        {
                            theoryDisplayName = ((Test.IReadableTestCase)convertedDataRow[0]).TestCase();
                        }
                        else
                        {
                            theoryDisplayName = TestCase.TestMethod.Method.GetDisplayNameWithArguments(DisplayName, convertedDataRow, resolvedTypes);
                        }
                        
                        var test = new XunitTest(TestCase, theoryDisplayName);
                        var skipReason = SkipReason ?? dataAttribute.GetNamedArgument<string>("Skip");
                        _testRunners.Add(new XunitTestRunner(test, MessageBus, TestClass, ConstructorArguments, methodToRun, convertedDataRow, skipReason, BeforeAfterAttributes, Aggregator, CancellationTokenSource));
                    }
                }
            }
            catch (Exception ex)
            {
                // Stash the exception so we can surface it during RunTestAsync
                _dataDiscoveryException = ex;
            }
        }

        protected override async Task<RunSummary> RunTestAsync()
        {
            if (_dataDiscoveryException != null)
                return RunTest_DataDiscoveryException();

            var runSummary = new RunSummary();
            foreach (var testRunner in _testRunners)
                runSummary.Aggregate(await testRunner.RunAsync());

            // Run the cleanup here so we can include cleanup time in the run summary,
            // but save any exceptions so we can surface them during the cleanup phase,
            // so they get properly reported as test case cleanup failures.
            var timer = new ExecutionTimer();
            foreach (var disposable in _toDispose)
                timer.Aggregate(() => _cleanupAggregator.Run(disposable.Dispose));

            runSummary.Time += timer.Total;
            return runSummary;
        }

        RunSummary RunTest_DataDiscoveryException()
        {
            var test = new XunitTest(TestCase, DisplayName);

            if (!MessageBus.QueueMessage(new TestStarting(test)))
                CancellationTokenSource.Cancel();
            else if (!MessageBus.QueueMessage(new TestFailed(test, 0, null, _dataDiscoveryException.Unwrap())))
                CancellationTokenSource.Cancel();
            if (!MessageBus.QueueMessage(new TestFinished(test, 0, null)))
                CancellationTokenSource.Cancel();

            return new RunSummary { Total = 1, Failed = 1 };
        }
    }


    public static class SerializationHelper
    {
        internal static Type GetType(string assemblyName, string typeName)
        {
            Assembly assembly = null;
            try
            {
                // Make sure we only use the short form
                var an = new AssemblyName(assemblyName);
                assembly = Assembly.Load(new AssemblyName { Name = an.Name, Version = an.Version });

            }
            catch { }

            if (assembly == null)
                return null;

            return assembly.GetType(typeName);
        }
    }

    public static class ExceptionExtensions
    {
        public static Exception Unwrap(this Exception ex)
        {
            while (true)
            {
                var tiex = ex as TargetInvocationException;
                if (tiex == null)
                    return ex;

                ex = tiex.InnerException;
            }
        }
    }


}
