using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    /// <summary>
    /// 
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="sInput"></param>
        /// <returns></returns>
        public static string RemoveBakets(this string sInput)
        {
            return sInput?.Replace("[", "").Replace("]", "");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sInputs"></param>
        /// <returns></returns>
        public static List<string> RemoveBakets(this List<string> sInputs)
        {
            return sInputs.Select(s => s.RemoveBakets()).ToList();
        }
    }
}