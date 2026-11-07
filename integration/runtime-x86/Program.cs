using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using FluentValidation;

if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
{
    throw new InvalidOperationException("This acceptance probe must execute in the native x86 runtime.");
}

Console.WriteLine($"Runtime qualification: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
try
{
    ExceptionCollectionProbe.Run(collectInsideCatch: false);
    Console.WriteLine("PASS compacting collection after catch: retained delegates and objects");
    ExceptionCollectionProbe.Run(collectInsideCatch: true);
    Console.WriteLine("PASS compacting collection inside catch: retained delegates and objects");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL exception/collection qualification: {error}");
    return 1;
}

var sample = new Sample { Score = 1.5 };
Expression<Func<Sample, double?>> expression = item => item.Score;
var accessors = new (string Name, Func<Sample, double?> Read)[]
{
    ("direct", item => item.Score),
    ("compiled", expression.Compile()),
    ("interpreted", expression.Compile(preferInterpretation: true))
};

foreach (var accessor in accessors)
{
    for (var iteration = 0; iteration < 1_000_000; iteration++)
    {
        var actual = accessor.Read(sample);
        if (!actual.HasValue || actual.Value != 1.5)
        {
            throw new InvalidOperationException($"{accessor.Name} corrupted the nullable return value.");
        }
    }

    Console.WriteLine($"PASS {accessor.Name}: 1,000,000 correct nullable returns");
}

var validator = new SampleValidator();
for (var iteration = 0; iteration < 1_000_000; iteration++)
{
    if (!validator.Validate(sample).IsValid)
    {
        throw new InvalidOperationException("Validation rejected the valid nullable value.");
    }
}

if (validator.Validate(new Sample { Score = -1 }).IsValid || validator.Validate(new Sample()).IsValid)
{
    throw new InvalidOperationException("Validation accepted an invalid nullable value.");
}

Console.WriteLine("PASS validation: 1,000,000 valid results; negative and missing values rejected");

var nested = new ParentSample { Child = new ChildSample { Ratio = 1.5 } };
var nestedValidator = new ParentSampleValidator();
for (var iteration = 0; iteration < 1_000_000; iteration++)
{
    if (!nestedValidator.Validate(nested).IsValid)
    {
        throw new InvalidOperationException("Nested validation rejected the valid nullable value.");
    }
}

if (nestedValidator.Validate(new ParentSample { Child = new ChildSample { Ratio = -1 } }).IsValid ||
    nestedValidator.Validate(new ParentSample { Child = new ChildSample() }).IsValid)
{
    throw new InvalidOperationException("Nested validation accepted an invalid nullable value.");
}

Console.WriteLine("PASS nested validation: 1,000,000 valid results; negative and missing values rejected");
return 0;

static class ExceptionCollectionProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Raise() => throw new IOException("Expected qualification exception");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(bool collectInsideCatch)
    {
        var first = new Marker("first");
        var second = new Marker("second");
        Action verify = () =>
        {
            if (first.Text != "first" || second.Text != "second")
            {
                throw new InvalidOperationException("Collection corrupted a retained marker.");
            }
        };

        for (var iteration = 0; iteration < 128; iteration++)
        {
            try
            {
                Raise();
            }
            catch (IOException)
            {
                if (collectInsideCatch)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            }

            if (!collectInsideCatch)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            verify();
        }
    }

    private sealed class Marker(string text)
    {
        public string Text { get; } = text;
    }
}

sealed class Sample
{
    public double? Score { get; set; }
}

sealed class SampleValidator : AbstractValidator<Sample>
{
    public SampleValidator()
    {
        RuleFor(item => item.Score).NotNull().GreaterThanOrEqualTo(0);
    }
}

sealed class ParentSample
{
    public ChildSample Child { get; set; } = new();
}

sealed class ChildSample
{
    public double? Ratio { get; set; }
}

sealed class ParentSampleValidator : AbstractValidator<ParentSample>
{
    public ParentSampleValidator()
    {
        RuleFor(item => item.Child).SetValidator(_ => new ChildSampleValidator());
    }
}

sealed class ChildSampleValidator : AbstractValidator<ChildSample>
{
    public ChildSampleValidator()
    {
        RuleFor(item => item.Ratio).NotNull().GreaterThanOrEqualTo(0);
    }
}
