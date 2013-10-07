//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Microsoft.Boogie;

namespace GPUVerify {
  public class GVCommandLineOptions : CommandLineOptions {

    public string ArrayToCheck = null;
    public bool NoSourceLocInfer = false;
    public bool OnlyIntraGroupRaceChecking = false;
    public bool DebugGPUVerify = false;
    // Dimensionality of block = BlockHighestDim + 1
    public int BlockHighestDim = 2;
    // Dimensionality of grid = GridHighestDim + 1
    public int GridHighestDim = 2;

    public GVCommandLineOptions() :
      base("GPUVerify", "GPUVerify kernel analyser") {
    }

    protected override bool ParseOption(string name, CommandLineOptionEngine.CommandLineParseState ps) {

      if (name == "blockHighestDim") {
        ps.GetNumericArgument(ref BlockHighestDim, 3);
        return true;
      }

      if (name == "gridHighestDim") {
        ps.GetNumericArgument(ref GridHighestDim, 3);
        return true;
      }

      if (name == "debugGPUVerify") {
        DebugGPUVerify = true;
        return true;
      }

      if (name == "noSourceLocInfer") {
        NoSourceLocInfer = true;
        return true;
      }

      if (name == "onlyIntraGroupRaceChecking") {
        OnlyIntraGroupRaceChecking = true;
        return true;
      }

      return base.ParseOption(name, ps);  // defer to superclass
    }
  }
}
