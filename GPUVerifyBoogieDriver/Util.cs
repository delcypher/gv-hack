//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


using System;
using Microsoft.Boogie;

namespace GPUVerify
{
  public class CheckForQuantifiers : StandardVisitor
  {
    bool quantifiersExist;

    private CheckForQuantifiers()
    {
      quantifiersExist = false;
    }

    public override QuantifierExpr VisitQuantifierExpr(QuantifierExpr node)
    {
      node = base.VisitQuantifierExpr(node);
      quantifiersExist = true;
      return node;
    }

    public static bool Found(Program node)
    {
      var cfq = new CheckForQuantifiers();
      cfq.VisitProgram(node);
      return cfq.quantifiersExist;
    }
  }
}

