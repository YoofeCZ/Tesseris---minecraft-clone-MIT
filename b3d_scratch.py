import struct, sys, math
import numpy as np

def q2m(q):  # OpenTK Matrix4.CreateFromQuaternion, row-vector convention
    x,y,z,w=q
    n=math.sqrt(x*x+y*y+z*z+w*w) or 1.0
    x,y,z,w=x/n,y/n,z/n,w/n
    m=np.identity(4)
    m[0,0]=1-2*(y*y+z*z); m[0,1]=2*(x*y+z*w); m[0,2]=2*(x*z-y*w)
    m[1,0]=2*(x*y-z*w);   m[1,1]=1-2*(x*x+z*z); m[1,2]=2*(y*z+x*w)
    m[2,0]=2*(x*z+y*w);   m[2,1]=2*(y*z-x*w); m[2,2]=1-2*(x*x+y*y)
    return m

def local(pos,scale,rot):
    S=np.identity(4); S[0,0],S[1,1],S[2,2]=scale
    R=q2m(rot)
    T=np.identity(4); T[3,0:3]=pos
    return S@R@T   # OpenTK: CreateScale*CreateFromQuaternion*CreateTranslation

def xf(m,v):
    return (np.array([v[0],v[1],v[2],1.0])@m)[0:3]

class Node:
    def __init__(s,name,parent):
        s.name=name; s.parent=parent; s.keys=[]; s.bp=(0,0,0); s.bs=(1,1,1); s.br=(0,0,0,1)

class Model:
    pass

def load(path):
    data=open(path,'rb').read()
    M=Model(); M.nodes=[]; M.verts=[]; M.weights=[]  # weights[i] = list of (node, strength)
    st={'lastMeshStart':0}
    def rd_str(o):
        e=data.index(b'\0',o); return data[o:e].decode('utf8','replace'), e+1
    def read_chunks(off,end,parent):
        while off<end:
            name=data[off:off+4].decode('ascii'); size=struct.unpack_from('<i',data,off+4)[0]
            cs=off+8; ce=cs+size
            if name=='NODE':
                nm,p=rd_str(cs)
                t=struct.unpack_from('<3f',data,p); p+=12
                s=struct.unpack_from('<3f',data,p); p+=12
                r=struct.unpack_from('<4f',data,p); p+=16
                n=Node(nm,parent); n.bp=t; n.bs=s; n.br=(r[1],r[2],r[3],r[0])
                idx=len(M.nodes); M.nodes.append(n)
                read_chunks(p,ce,idx)
            elif name=='MESH':
                st['lastMeshStart']=len(M.verts)
                read_chunks(cs+4,ce,parent)
            elif name=='VRTS':
                flags,ncs,css=struct.unpack_from('<3i',data,cs); p=cs+12
                per=3+(3 if flags&1 else 0)+(4 if flags&2 else 0)+ncs*css
                while p<ce:
                    v=struct.unpack_from('<3f',data,p)
                    M.verts.append(v); M.weights.append([])
                    p+=per*4
            elif name=='BONE':
                p=cs
                while p<ce:
                    vi,strength=struct.unpack_from('<if',data,p); p+=8
                    vi+=st['lastMeshStart']
                    if strength>0: M.weights[vi].append((parent,strength))
            elif name=='KEYS':
                flags=struct.unpack_from('<i',data,cs)[0]; p=cs+4
                while p<ce:
                    f=struct.unpack_from('<i',data,p)[0]; p+=4
                    pos=sc=rot=None
                    if flags&1: pos=struct.unpack_from('<3f',data,p); p+=12
                    if flags&2: sc=struct.unpack_from('<3f',data,p); p+=12
                    if flags&4:
                        r=struct.unpack_from('<4f',data,p); p+=16; rot=(r[1],r[2],r[3],r[0])
                    M.nodes[parent].keys.append((float(f),pos,sc,rot))
            off=ce
    hs=struct.unpack_from('<i',data,4)[0]
    read_chunks(12,8+hs,-1)
    # bind globals
    M.bind=[]
    for i,n in enumerate(M.nodes):
        L=local(n.bp,n.bs,n.br)
        M.bind.append(L if n.parent<0 else L@M.bind[n.parent])
    M.invbind=[np.linalg.inv(b) for b in M.bind]
    return M

def interp(keys,frame,sel,fb):
    prev=nxt=None
    for k in keys:
        if k[sel] is None: continue
        if k[0]<=frame: prev=k
        if k[0]>=frame and nxt is None: nxt=k
    if prev is None and nxt is None: return fb
    if prev is None: return nxt[sel]
    if nxt is None or nxt[0]==prev[0]: return prev[sel]
    t=(frame-prev[0])/(nxt[0]-prev[0])
    a=np.array(prev[sel]); b=np.array(nxt[sel])
    if sel==3:
        if np.dot(a,b)<0: b=-b
        d=np.dot(a,b); d=max(-1,min(1,d))
        th=math.acos(d)
        if th<1e-4: q=a+(b-a)*t
        else: q=(math.sin((1-t)*th)*a+math.sin(t*th)*b)/math.sin(th)
        return tuple(q/np.linalg.norm(q))
    return tuple(a+(b-a)*t)

def animglobals(M,frame):
    res=[]
    for i,n in enumerate(M.nodes):
        p=interp(n.keys,frame,1,n.bp)
        s=interp(n.keys,frame,2,n.bs)
        r=interp(n.keys,frame,3,n.br)
        L=local(p,s,r)
        res.append(L if n.parent<0 else L@res[n.parent])
    return res

def top4(ws):
    """Irrlicht WeightBuffer::addWeight: slots init 0, replace first min if weight >= min."""
    jid=[0,0,0,0]; w=[0.0,0.0,0.0,0.0]
    for n,s in ws:
        mi=min(range(4),key=lambda k:w[k])  # min_element -> first minimum
        if w[mi]>s: continue
        w[mi]=s; jid[mi]=n
    tot=sum(w)
    if tot==0: return []
    return [(jid[k],w[k]/tot) for k in range(4) if w[k]!=0.0]

def skin(M,ag,ws,v):
    if not ws: return np.array(v)
    r=np.zeros(3); tot=0.0
    for n,s in ws:
        bl=xf(M.invbind[n],v)
        r+=xf(ag[n],bl)*s; tot+=s
    return r/tot if tot>1e-6 else np.array(v)
