//xfail:BOOGIE_ERROR
//--local_size=128 --num_groups=128
//kernel.cl:13:[\d]+:[\s]+error:[\s]+this assertion might not hold for thread [\d]+ in group [\d]+[\s]+__assert\(!__read\(q\)\);





__kernel void foo(__local int* p, __local int* q) {

  p[get_local_id(0)] = q[get_local_id(0)];

  __assert(!__read(q));

}
