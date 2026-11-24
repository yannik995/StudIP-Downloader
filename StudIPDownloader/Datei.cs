using System;

namespace StudIPDownloader
{
    class Datei
    {
        public string Id;
        public string Filename;
        public long Size;
        public DateTimeOffset? ChangeDate;

        public Datei(string id, string filename, long size, DateTimeOffset? changeDate)
        {
            Id = id;
            Filename = filename;
            Size = size;
            ChangeDate = changeDate;
        }
    }
}
