//xfail:BOOGIE_ERROR
//--local_size=1024 --num_groups=1024 --no-inline
//Read by work item[\s]+[\d]+[\s]+in work group[\s]+[\d].+kernel.cl:21:5:[\s]+y = p\[0];
//Write by work item[\s]+[\d]+[\s]+in work group[\s]+[\d].+kernel.cl:15:12:+[\s]+p\[0] = get_local_id\(0\);



__kernel void foo(__local int* p) {

  volatile int x, y;

  x = get_local_id(0) == 0 ? 0 : CLK_LOCAL_MEM_FENCE;

  if(get_local_id(0) == 0) {
    p[0] = get_local_id(0);
  }

  barrier(x);  

  if(get_local_id(0) == 1) {
    y = p[0];
  }

}
