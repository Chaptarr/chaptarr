using DryIoc;
using NzbDrone.Core.Organizer.NamingPattern;

namespace NzbDrone.Core.Organizer
{
    public static class CompositionExtensions
    {
        public static IContainer AddNamingPatternServices(this IContainer container)
        {
            container.Register<IPatternCompiler, PatternCompiler>(Reuse.Singleton);
            container.Register<INamingPatternService, NamingPatternService>(Reuse.Singleton);

            return container;
        }
    }
}