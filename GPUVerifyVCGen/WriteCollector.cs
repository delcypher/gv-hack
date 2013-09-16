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
using Microsoft.Basetypes;

namespace GPUVerify
{

    class WriteCollector : AccessCollector
    {

        private AccessRecord access = null;

        public WriteCollector(IKernelArrayInfo State)
            : base(State)
        {
        }

        private bool NoWrittenVariable()
        {
            return access == null;
        }

        public override AssignLhs VisitMapAssignLhs(MapAssignLhs node)
        {
            Debug.Assert(NoWrittenVariable());

            if (!State.ContainsNonLocalArray(node.DeepAssignedVariable))
            {
                return node;
            }

            Variable WrittenVariable = node.DeepAssignedVariable;

            CheckMapIndex(node);
            Debug.Assert(!(node.Map is MapAssignLhs));

            access = new AccessRecord(WrittenVariable, node.Indexes[0]);

            return node;
        }

        private void CheckMapIndex(MapAssignLhs node)
        {
            if (node.Indexes.Count > 1)
            {
                MultiDimensionalMapError();
            }
        }

        internal bool FoundWrite()
        {
            return access != null;
        }

        internal AccessRecord GetAccess()
        {
            return access;
        }

    }
}
