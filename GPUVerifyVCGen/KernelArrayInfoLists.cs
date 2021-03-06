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
using System.Diagnostics;

namespace GPUVerify
{
    class KernelArrayInfoLists : IKernelArrayInfo
    {
        private List<Variable> GlobalVariables;
        private List<Variable> GroupSharedVariables;
        private List<Variable> ConstantVariables;
        private List<Variable> PrivateVariables;
        private List<Variable> ReadOnlyNonLocalVariables;

        public KernelArrayInfoLists()
        {
            GlobalVariables = new List<Variable>();
            GroupSharedVariables = new List<Variable>();
            ConstantVariables = new List<Variable>();
            PrivateVariables = new List<Variable>();
            ReadOnlyNonLocalVariables = new List<Variable>();
        }

        public ICollection<Variable> getGlobalArrays()
        {
            return GlobalVariables;
        }

        public ICollection<Variable> getGroupSharedArrays()
        {
            return GroupSharedVariables;
        }

        public ICollection<Variable> getConstantArrays()
        {
            return ConstantVariables;
        }

        public ICollection<Variable> getPrivateArrays()
        {
            return PrivateVariables;
        }

        public ICollection<Variable> getAllNonLocalArrays()
        {
            List<Variable> all = new List<Variable>();
            all.AddRange(GlobalVariables);
            all.AddRange(GroupSharedVariables);
            return all;
        }

        public ICollection<Variable> getReadOnlyNonLocalArrays()
        {
            return ReadOnlyNonLocalVariables;
        }

        public ICollection<Variable> getAllArrays()
        {
            List<Variable> all = new List<Variable>();
            all.AddRange(getAllNonLocalArrays());
            all.AddRange(getConstantArrays());
            all.AddRange(PrivateVariables);
            return all;
        }

        public bool ContainsNonLocalArray(Variable v)
        {
            return getAllNonLocalArrays().Contains(v);
        }

        public bool ContainsConstantArray(Variable v)
        {
            return ConstantVariables.Contains(v);
        }

    }
}
