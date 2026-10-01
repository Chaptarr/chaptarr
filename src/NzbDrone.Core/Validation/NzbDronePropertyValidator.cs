using System;
using System.Collections.Generic;
using FluentValidation;
using FluentValidation.Validators;

namespace NzbDrone.Core.Validation
{
    public abstract class NzbDronePropertyValidator
    {
        protected abstract string GetDefaultMessageTemplate();

        protected abstract bool IsValid(NzbDronePropertyValidatorContext context);

        internal string DefaultMessageTemplate => GetDefaultMessageTemplate();

        internal bool Validate(NzbDronePropertyValidatorContext context)
        {
            return IsValid(context);
        }
    }

    public sealed class NzbDronePropertyValidatorContext
    {
        internal NzbDronePropertyValidatorContext(
            object propertyValue,
            object instanceToValidate,
            IDictionary<string, object> rootContextData,
            Action<string, object> appendMessageArgument)
        {
            PropertyValue = propertyValue;
            InstanceToValidate = instanceToValidate;
            RootContextData = rootContextData;
            MessageFormatter = new NzbDroneMessageFormatter(appendMessageArgument);
        }

        public object PropertyValue { get; }

        public object InstanceToValidate { get; }

        public IDictionary<string, object> RootContextData { get; }

        public NzbDronePropertyValidatorContext ParentContext => this;

        public NzbDroneMessageFormatter MessageFormatter { get; }
    }

    public sealed class NzbDroneMessageFormatter
    {
        private readonly Action<string, object> _appendArgument;

        internal NzbDroneMessageFormatter(Action<string, object> appendArgument)
        {
            _appendArgument = appendArgument;
        }

        public void AppendArgument(string name, object value)
        {
            _appendArgument(name, value);
        }
    }

    internal sealed class NzbDronePropertyValidatorAdapter<T, TProperty> : PropertyValidator<T, TProperty>
    {
        private readonly NzbDronePropertyValidator _validator;

        public NzbDronePropertyValidatorAdapter(NzbDronePropertyValidator validator)
        {
            _validator = validator;
        }

        public override string Name => _validator.GetType().Name;

        protected override string GetDefaultMessageTemplate(string errorCode)
        {
            return _validator.DefaultMessageTemplate;
        }

        public override bool IsValid(ValidationContext<T> context, TProperty value)
        {
            var legacyContext = new NzbDronePropertyValidatorContext(
                value,
                context.InstanceToValidate,
                context.RootContextData,
                (name, argument) =>
                {
                    context.MessageFormatter.AppendArgument(name, argument);
                });

            return _validator.Validate(legacyContext);
        }
    }

    public static class NzbDronePropertyValidatorExtensions
    {
        public static IRuleBuilderOptions<T, TProperty> SetValidator<T, TProperty>(
            this IRuleBuilder<T, TProperty> ruleBuilder,
            NzbDronePropertyValidator validator)
        {
            return ruleBuilder.SetValidator(new NzbDronePropertyValidatorAdapter<T, TProperty>(validator));
        }
    }
}
