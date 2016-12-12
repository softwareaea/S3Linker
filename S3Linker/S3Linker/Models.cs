using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace S3Linker
{
    public class Folder
    {
        public string Id { get; set; }
        public string Prefix { get; set; }
        public DateTime ExpirationTime { get; set; }
    }

    public class Entry
    {
        public string Url { get; set; }
        public string RelativePath { get; set; }
        public bool IsFolder { get; set; }
        public long FileSize { get; set; }
    }
}
