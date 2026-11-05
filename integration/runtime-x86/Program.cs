using System.Linq.Expressions;
using System.Runtime.InteropServices;
using FluentValidation;

if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
{
    throw new InvalidOperationException("This acceptance probe must execute in the native x86 runtime.");
}

Console.WriteLine($"Runtime qualification: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
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
