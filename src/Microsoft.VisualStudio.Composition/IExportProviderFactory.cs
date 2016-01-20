﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    public interface IExportProviderFactory
    {
        ExportProvider CreateExportProvider();

        ExportProvider CreateExportProvider(IExceptionRecorder exceptionCallback);
    }
}
