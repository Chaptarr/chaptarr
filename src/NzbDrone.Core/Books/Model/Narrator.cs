using System;
using System.Collections.Generic;
using Equ;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class Narrator : Entity<Narrator>
    {
        public Narrator()
        {
            Tags = new HashSet<int>();
            Metadata = new NarratorMetadata();
        }

        // These correspond to columns in the Narrators table
        public int NarratorMetadataId { get; set; }
        public string CleanName { get; set; }
        public bool Monitored { get; set; }
        public NewItemMonitorTypes MonitorNewItems { get; set; }
        public DateTime? LastInfoSync { get; set; }
        public string Path { get; set; }
        public string RootFolderPath { get; set; }
        public DateTime Added { get; set; }
        public HashSet<int> Tags { get; set; }
        [MemberwiseEqualityIgnore]
        public AddNarratorOptions AddOptions { get; set; }
        // Dynamically loaded from DB
        [MemberwiseEqualityIgnore]
        public LazyLoaded<NarratorMetadata> Metadata { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<List<Book>> Books { get; set; }
        [MemberwiseEqualityIgnore]
        public LazyLoaded<List<Series>> Series { get; set; }

        //compatibility properties
        [MemberwiseEqualityIgnore]
        public string Name
        {
            get { return Metadata.Value.Name; }
            set { Metadata.Value.Name = value; }
        }

        public override string ToString()
        {
            return string.Format("[{0}][{1}]", Id, Metadata.Value.Name.NullSafe());
        }

        public override void UseMetadataFrom(Narrator other)
        {
            CleanName = other.CleanName;
        }

        public override void UseDbFieldsFrom(Narrator other)
        {
            Id = other.Id;
            NarratorMetadataId = other.NarratorMetadataId;
            Monitored = other.Monitored;
            MonitorNewItems = other.MonitorNewItems;
            LastInfoSync = other.LastInfoSync;
            Path = other.Path;
            RootFolderPath = other.RootFolderPath;
            Added = other.Added;
            Tags = other.Tags;
            AddOptions = other.AddOptions;
        }

        public override void ApplyChanges(Narrator other)
        {
            Path = other.Path;
            RootFolderPath = other.RootFolderPath;
            Monitored = other.Monitored;
            MonitorNewItems = other.MonitorNewItems;
            Books = other.Books;
            Tags = other.Tags;
            AddOptions = other.AddOptions;
        }
    }
}
