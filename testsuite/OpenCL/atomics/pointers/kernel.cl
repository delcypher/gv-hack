//xfail:BOOGIE_ERROR
//--local_size=1024 --num_groups=1
//kernel.cl: error: possible atomic-write race on \(\(char\*\)A\)\[48\]:
//Write by thread 12 in group 0
//Atomic by thread \d+ in group 0

kernel void pointers (local int* A, local int* B, int c)
{
	local int* p;
	int tid = get_local_id(0);
	if (c)
		p = B + 3;
	else
		p = A + 12;
	A[tid] = atomic_inc(p);
}
