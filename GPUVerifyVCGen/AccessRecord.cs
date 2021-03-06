//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.Boogie;

namespace GPUVerify
{
    class AccessRecord
    {
        public Variable v;
        public Expr Index;

        public AccessRecord(Variable v, Expr Index)
        {
            this.v = v;
            this.Index = Index;
        }

    }

}
