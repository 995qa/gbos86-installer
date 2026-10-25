// Development helper: prints what DiskService.LoadDisks sees. Read-only.
using System;

namespace GbOs86
{
    internal static class DiskProbe
    {
        private static void Main()
        {
            try
            {
                System.Collections.Generic.List<DiskInfo> disks = DiskService.LoadDisks();
                Console.WriteLine("disks=" + disks.Count);
                foreach (DiskInfo d in disks)
                {
                    Console.WriteLine("Disk " + d.Number + " | " + d.Model + " | " + d.Serial +
                                      " | " + d.Size + " | " + d.PartitionStyle + " | " + d.BusType +
                                      " | sys=" + d.IsSystem + " boot=" + d.IsBoot +
                                      " free=" + (d.LargestFree == null ? 0 : d.LargestFree.Length));
                    foreach (PartitionInfo p in d.Partitions)
                    {
                        Console.WriteLine("  p" + p.Number + " off=" + p.Offset + " size=" + p.Size +
                                          " type=" + p.Type + " guid=" + p.Guid +
                                          " paths=" + p.AccessPaths);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex);
            }
        }
    }
}
