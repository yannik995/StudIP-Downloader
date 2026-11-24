using System;

namespace StudIPDownloader
{
    class Semester
    {
        public string Id;
        public string Name;
        public DateTimeOffset? Start;
        public DateTimeOffset? End;

        public Semester(string id, string name, DateTimeOffset? start, DateTimeOffset? end)
        {
            Id = id;
            Name = name;
            Start = start;
            End = end;
        }
    }
}
