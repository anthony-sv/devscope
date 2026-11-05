# Draws the Devscope app icon. Usage: python tools/make-icon.py src/Devscope.App/Assets/devscope.ico  (needs Pillow)
import sys
from PIL import Image, ImageDraw
S=1024
def render(simple=False):
    img=Image.new("RGBA",(S,S),(0,0,0,0))
    # gradient background in rounded square
    grad=Image.new("RGBA",(S,S))
    top=(15,108,189); bot=(79,107,237)
    gd=ImageDraw.Draw(grad)
    for y in range(S):
        t=y/(S-1); c=tuple(int(top[i]*(1-t)+bot[i]*t) for i in range(3))
        gd.line([(0,y),(S,y)],fill=c+(255,))
    mask=Image.new("L",(S,S),0)
    ImageDraw.Draw(mask).rounded_rectangle([40,40,S-40,S-40],radius=210,fill=255)
    img.paste(grad,(0,0),mask)
    d=ImageDraw.Draw(img)
    # log lines
    lines=[(170,250,700,(255,255,255,235)),(170,370,560,(255,255,255,170)),(170,490,620,(255,120,120,255)),(170,610,430,(255,255,255,170))]
    if simple: lines=[lines[0],lines[2]]
    h=64 if not simple else 90
    for x,y,x2,c in lines:
        d.rounded_rectangle([x,y,x2,y+h],radius=h//2,fill=c)
    # magnifier
    cx,cy,r=600,560,185; w=70 if not simple else 90
    ov=Image.new("RGBA",(S,S),(0,0,0,0)); od=ImageDraw.Draw(ov)
    od.ellipse([cx-r,cy-r,cx+r,cy+r],fill=(255,255,255,70))
    img.alpha_composite(ov); d=ImageDraw.Draw(img)
    d.ellipse([cx-r,cy-r,cx+r,cy+r],outline=(255,255,255,255),width=w)
    import math
    a=math.radians(45); x1=cx+(r+w/2-10)*math.cos(a); y1=cy+(r+w/2-10)*math.sin(a)
    d.line([(x1,y1),(x1+150,y1+150)],fill=(255,255,255,255),width=110)
    d.ellipse([x1+150-55,y1+150-55,x1+150+55,y1+150+55],fill=(255,255,255,255))
    return img
big=render(); small=render(simple=True)
sizes=[256,128,64,48,32,24,16]
imgs=[(small if s<=32 else big).resize((s,s),Image.LANCZOS) for s in sizes]
imgs[0].save(sys.argv[1],sizes=[(s,s) for s in sizes],append_images=imgs[1:])
