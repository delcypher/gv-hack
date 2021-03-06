//xfail:BOOGIE_ERROR
//--local_size=1024 --num_groups=2 --no-inline
//Write by work item [\d]+ in work group [\d], .+kernel.cl:16:7:
//Read by work item [\d]+ in work group [\d], possible sources are:
//kernel.cl:12:9
//kernel.cl:13:9

__kernel void foo(__local int* p) {

    int x = 0;
    for(int i = 0; i < 100; i++) {
        x += p[i];
        x += p[i+1];
    }

    p[get_local_id(0)] = x;

}
