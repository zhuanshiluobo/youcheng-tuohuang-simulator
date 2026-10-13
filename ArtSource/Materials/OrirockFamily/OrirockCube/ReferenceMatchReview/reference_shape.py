"""参考图 180×180 坐标中的实体轮廓与断裂交界；不含背景和圆环。"""
import math
import numpy as np

PIXEL_UNIT=.012
ANCHORS=[
 ('TL',40,56,'Z',.565),
 ('BL0',61,41,'Z',.61),('BL1',73,31,'Z',.625),
 ('B0',81,34,'Z',.594),('B1',88,33,'Z',.61),('B2',94,29,'Z',.64),
 ('B3',105,34,'Z',.595),('B4',117,33,'Z',.615),('B5',130,40,'Z',.60),('B6',136,47,'Z',.57),
 ('RT',148,51,'X',.64),('TR',131,71,'X',.64),
 ('L0',46,66,'Y',-.66),('L1',54,72,'Y',-.68),('L2',67,77,'Y',-.66),
 ('L3',82,79,'Y',-.69),('L4',92,74,'Y',-.66),('L5',94,65,'Z',.55),
 ('L6',101,68,'Y',-.65),('L7',114,74,'Y',-.61),('L8',125,73,'X',.64),
 ('C0',39,71,'Y',-.61),('C1',35,77,'Y',-.61),('C2',46,76,'Y',-.64),
 ('C3',57,80,'Y',-.61),('C4',68,83,'Y',-.59),('C5',81,82,'Y',-.60),
 ('C6',90,87,'Y',-.58),('C7',91,96,'Y',-.58),('C8',101,104,'Y',-.61),
 ('C9',96,112,'Y',-.57),('C10',108,115,'Y',-.59),
 ('S0',125,79,'X',.64),('S1',121,84,'X',.64),('S2',121,96,'X',.64),
 ('S3',131,89,'X',.64),('S4',128,98,'X',.64),('S5',122,105,'X',.64),
 ('S6',119,110,'X',.635),('S7',117,115,'X',.64),
 ('M0',103,84,'Y',-.62),('M1',106,95,'Y',-.61),('M2',113,108,'Y',-.60),
 ('F0',38,111,'Y',-.61),('F1',41,123,'Y',-.62),('F2',52,130,'Y',-.62),
 ('F3',74,136,'Y',-.64),('F4',96,139,'Y',-.61),('FB',119,138,'X',.64),
 ('I0',53,96,'Y',-.65),('I1',64,109,'Y',-.67),('I2',73,120,'Y',-.64),('I3',90,128,'Y',-.65),
 ('R1',148,94,'X',.64),('R2',140,97,'X',.64),('R3',139,119,'X',.64),
]
CHIP=[('a',31,125,'Y',-.69),('b',36,120,'Y',-.65),('c',43,117,'Y',-.63),
 ('d',49,122,'Y',-.70),('e',53,129,'Y',-.74),('f',51,137,'Y',-.73),
 ('g',42,138,'Y',-.75),('h',31,132,'Y',-.73),('i',43,126,'Y',-.80)]
BACK=['BL0','BL1','B0','B1','B2','B3','B4','B5','B6']
LIP=['L'+str(i) for i in range(9)]
LOWER=['C'+str(i) for i in range(11)]
SEAM=['S'+str(i) for i in range(8)]
TOP=['TL']+BACK+['RT','TR']+list(reversed(LIP))

def axes(azimuth,elevation):
    a,e=math.radians(azimuth),math.radians(elevation)
    toward=np.array([math.sin(a)*math.cos(e),-math.cos(a)*math.cos(e),math.sin(e)])
    right=np.array([math.cos(a),math.sin(a),0])
    up=np.cross(toward,right)
    return toward,right,up

def build_points(azimuth=25,elevation=25):
    toward,right,up=axes(azimuth,elevation)
    points={}
    cap_heights={'L0':.50,'L1':.44,'L2':.46,'L3':.455,'L4':.48,'L5':.55,'L6':.54,'L7':.50,'L8':.49}
    for name,x,y,plane,value in ANCHORS+CHIP:
        if plane=='Y': value+=.18
        if name in cap_heights: plane,value='Z',cap_heights[name]
        p=right*((x-90)*PIXEL_UNIT)+up*((90-y)*PIXEL_UNIT)
        axis='XYZ'.index(plane)
        p+=toward*((value-p[axis])/toward[axis])
        points[name]=p
    return points

if __name__=='__main__':
    candidates=[]
    for az in np.linspace(20,36,65):
        for el in np.linspace(18,32,57):
            p=build_points(float(az),float(el))
            coords=np.array([p[a[0]] for a in ANCHORS])
            lo,hi=coords.min(axis=0),coords.max(axis=0)
            size=hi-lo
            # 整体接近正方体，主侧面顶部无整体坡度。
            top_tilt=abs(p['RT'][2]-p['TR'][2])
            score=np.std(size)/np.mean(size)+top_tilt*.50
            candidates.append((score,float(az),float(el),size.tolist(),top_tilt))
    for item in sorted(candidates)[:8]: print(item)
