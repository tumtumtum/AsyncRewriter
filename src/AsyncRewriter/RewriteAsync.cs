﻿#if NET45
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AsyncRewriter.Logging;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsyncRewriter
{
    /// <summary>
    /// </summary>
    /// <remarks>
    /// http://stackoverflow.com/questions/2961753/how-to-hide-files-generated-by-custom-tool-in-visual-studio
    /// </remarks>
    public class RewriteAsync : Microsoft.Build.Utilities.Task
    {
        [Required]
        public ITaskItem[] InputFiles { get; set; }
        [Required]
        public ITaskItem OutputFile { get; set; }

		public ITaskItem[] Assemblies { get; set; }

        readonly Rewriter _rewriter;

        public RewriteAsync()
        {
            _rewriter = Log == null ? new Rewriter() : new Rewriter(new TaskLoggingAdapter(Log));
        }

		public override bool Execute()
        {
	        var asyncCode = _rewriter.RewriteAndMerge(InputFiles.Select(f => f.ItemSpec).ToArray(), Assemblies?.Select(c => c.ItemSpec).ToArray());
            File.WriteAllText(OutputFile.ItemSpec, asyncCode);
            return true;
        }
    }
}
#endif