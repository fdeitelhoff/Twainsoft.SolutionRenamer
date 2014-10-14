﻿using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Twainsoft.SolutionRenamer.VSPackage.VSX
{
    public static class StatusBarHelper
    {
        public static void Update(string text)
        {
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;

            if (statusBar == null)
            {
                throw new InvalidOperationException("Cannot Find The StatusBar.");
            }

            int frozen;

            statusBar.IsFrozen(out frozen);

            if (frozen == 0)
            {
                statusBar.SetText(text);
            }
        }
    }
}