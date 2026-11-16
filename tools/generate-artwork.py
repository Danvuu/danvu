"""Generate Danvu's original demo artwork; no external art or audio is used.

Requires Python 3, Pillow and numpy. Run from any directory. Deterministic seeds
make this source reproducible. Output PNGs are already committed for app builds.
"""
from pathlib import Path
import json
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parents[1] / 'src/Danvu.Windows/Assets/Artwork'
OUT.mkdir(parents=True, exist_ok=True)
N = 1000
Y, X = np.mgrid[0:N, 0:N].astype(float) / N
SERIF = '/usr/share/fonts/opentype/urw-base35/P052-Italic.otf'
SANS = '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'
ART = {
 'afterglow': ('Afterglow', ['#ef593f', '#f5a360', '#3e1d31']),
 'blue-hour': ('Blue Hour', ['#304389', '#8495cc', '#161b43']),
 'soft-focus': ('Soft Focus', ['#d69bab', '#f1cec3', '#745e8d']),
 'velvet-sky': ('Velvet Sky', ['#6d244b', '#c37595', '#241a32']),
 'currents': ('Currents', ['#267e7f', '#9bc5b2', '#102d3c']),
 'midnight-radio': ('Midnight Radio', ['#514783', '#a9b4cf', '#181725']),
 'daydream': ('Daydream', ['#e8b399', '#d58d7b', '#7f748c']),
 'golden': ('Golden', ['#e6ad48', '#f3d795', '#9b5d36']),
 'bloom': ('Bloom', ['#da606c', '#f9afa5', '#52223b']),
 'satellites': ('Satellites', ['#5f7c9e', '#adc8cc', '#1c293f']),
}

def color(hexstr):
 return np.array([int(hexstr[i:i+2], 16) for i in (1,3,5)], dtype=float)

def blend(a,b,t):
 t=np.clip(t,0,1)
 return a*(1-t[...,None])+b*t[...,None]

def base(palette, seed):
 rng=np.random.default_rng(seed)
 c=[color(v) for v in palette]
 grad=np.clip(Y*.9+X*.17,0,1)
 arr=blend(c[0],c[2],grad)
 glow=np.exp(-((X-.68)**2/.16+(Y-.23)**2/.22))
 arr=blend(arr,c[1],glow*.7)
 # Low-frequency analog grain gives print-like atmospheric detail.
 noise=rng.normal(0,2.8,(N,N,1))
 return arr+noise

def finish(arr, slug, title, seed, typeposition='bottom'):
 rng=np.random.default_rng(seed+101)
 edge=np.clip(1-((X-.5)**2+(Y-.5)**2)*.48,.65,1)
 arr=arr*edge[...,None]+rng.normal(0,1.7,(N,N,1))
 im=Image.fromarray(np.uint8(np.clip(arr,0,255))).convert('RGB')
 d=ImageDraw.Draw(im)
 try:
  serif=ImageFont.truetype(SERIF,72)
  small=ImageFont.truetype(SANS,16)
 except OSError:
  serif=ImageFont.truetype(SANS,64)
  small=ImageFont.truetype(SANS,16)
 if typeposition=='bottom':
  d.text((56,820),title,font=serif,fill=(249,239,222))
  d.text((59,918),'D A N V U   /   O R I G I N A L   S E S S I O N S',font=small,fill=(229,219,212))
 else:
  d.text((56,49),title,font=serif,fill=(249,239,222))
  d.text((59,147),'D A N V U   /   O R I G I N A L   S E S S I O N S',font=small,fill=(229,219,212))
 im=im.resize((600,600),Image.Resampling.LANCZOS)
 im.save(OUT/f'{slug}.png',optimize=True)
 return im

for i,(slug,(title,palette)) in enumerate(ART.items()):
 a=base(palette,i+31)
 c=[color(v) for v in palette]
 if slug=='afterglow':
  # Saturated sunset light over a gently undulating red dune.
  sun=np.sqrt((X-.64)**2+(Y-.36)**2)
  a=blend(a,np.array([255,204,125]),np.clip((.225-sun)*80,0,1)*.95)
  for n in range(4):
   ridge=.58+n*.075+.045*np.sin(X*5+n*1.2)+.025*np.sin(X*12+n)
   mask=Y>ridge
   surf=np.array([125-22*n,47-4*n,52-3*n])+np.maximum(0,Y-ridge)[...,None]*55
   a=np.where(mask[...,None],surf,a)
 elif slug=='blue-hour':
  # Mist-shrouded midnight orb and blue rippled water.
  r=np.sqrt(((X-.48)/1.05)**2+(Y-.38)**2)
  t=np.clip((.262-r)*30,0,1)
  sphere=blend(np.array([22,40,93]),np.array([151,173,221]),np.clip(.8-X+Y*.1,0,1))
  a=blend(a,sphere,t)
  mist=np.exp(-((Y-.58)*12)**2)*.66
  a=blend(a,np.array([128,147,182]),mist)
  water=np.sin(Y*130+np.sin(X*23)*2)*np.maximum(0,Y-.6)*20
  a+=water[...,None]
 elif slug=='soft-focus':
  # Pearlescent soft overlapping glass disks.
  for cx,cy,r,co in [(.25,.3,.33,[234,184,175]),(.66,.46,.32,[177,144,180]),(.45,.64,.28,[237,206,187])]:
   rr=np.sqrt((X-cx)**2+(Y-cy)**2)
   light=np.clip((r-rr)*32,0,1)
   shade=np.clip((X-cx)*.8+(Y-cy)*.8+.5,0,1)
   surf=blend(np.array(co)+25,np.array(co)*.55,shade)
   rim=np.exp(-((rr-r)*80)**2)
   a=blend(a,surf,light*.94)
   a+=rim[...,None]*25
 elif slug=='velvet-sky':
  # Sculptural ribbon, lit like folded velvet.
  q=Y-.51-.20*np.sin(X*4.4)
  v=np.sin(q*19+np.cos(X*7))*0.5+.5
  body=np.clip((.26-np.abs(q))*16,0,1)
  folded=blend(np.array([56,14,49]),np.array([222,136,167]),v**2)
  a=blend(a,folded,body)
 elif slug=='currents':
  # Light caustics on flowing teal water, with a refractive oval.
  wave=np.sin(X*18+Y*7+np.sin(Y*11)*2)+np.sin(Y*23-X*6)
  sheen=(np.clip(wave,0,2)/2)**8
  a+=sheen[...,None]*65
  rr=np.sqrt(((X-.52)*1.25)**2+((Y-.45)*.86)**2)
  ring=np.exp(-((rr-.28)*65)**2)
  a=blend(a,np.array([201,227,206]),ring*.8)
  shadow=np.exp(-((rr-.30)*35)**2)
  a-=shadow[...,None]*20
 elif slug=='midnight-radio':
  # A luminous freestanding monolith, with subtle vertical light bloom.
  portal=np.maximum(np.abs((X-.51)/.21),np.abs((Y-.44)/.29))
  halo=np.exp(-((portal-1)*6)**2)
  a+=halo[...,None]*np.array([43,52,79])
  inside=(portal<1)
  light=np.clip((X-.3)*1.7+(Y-.2)*.25,0,1)
  slab=blend(np.array([188,197,223]),np.array([47,40,73]),light)
  a=np.where(inside[...,None],slab,a)
  a+=np.exp(-((X-.3)*160)**2)[...,None]*np.array([27,35,48])
 elif slug=='daydream':
  # Fine peach cloud bands dissolving into a lilac horizon.
  for band in range(7):
   h=.18+band*.08+.028*np.sin(X*(7+band)+band)
   w=np.exp(-((Y-h)*(35-band*2))**2)
   a=blend(a,np.array([255-band*7,221-band*8,190-band*3]),w*.32)
  radius=np.sqrt((X-.66)**2+(Y-.35)**2)
  a=blend(a,np.array([255,231,194]),np.clip((.14-radius)*80,0,1)*.9)
 elif slug=='golden':
  # Folded, warm metallic satin.
  v=np.sin(X*10+np.sin(Y*5)*4)+.25*np.cos(X*23-Y*7)
  sheen=np.clip((v+1)/2,0,1)**2
  a=blend(np.array([93,51,28]),np.array([253,220,139]),sheen)
  a=blend(a,np.array([233,176,77]),Y*.2)
 elif slug=='bloom':
  # Radial satin petals, no external photography.
  dx,dy=X-.52,(Y-.43)*1.04
  r=np.sqrt(dx*dx+dy*dy)
  theta=np.arctan2(dy,dx)
  petal=(np.cos(theta*7+r*13)*.5+.5)
  envelope=np.clip((.38-r+.06*np.cos(theta*7))*18,0,1)
  light=np.clip(petal*.65+(1-r)*.24+np.sin(theta)*.12,0,1)
  petals=blend(np.array([108,29,62]),np.array([255,170,157]),light**1.7)
  a=blend(a,petals,envelope)
  core=np.exp(-((r/.045)**2))
  a=blend(a,np.array([241,184,119]),core)
 elif slug=='satellites':
  # Astral marble with a slender orbital ring.
  dx,dy=X-.5,Y-.43
  r=np.sqrt(dx*dx+dy*dy)
  theta=np.arctan2(dy,dx)
  marble=(np.sin(X*27+np.sin(Y*20)+Y*17)*.5+.5)
  sph=blend(np.array([38,65,91]),np.array([191,212,208]),np.clip(.5-dx+dy*.3+marble*.15,0,1))
  a=blend(a,sph,np.clip((.245-r)*90,0,1))
  xp=dx*np.cos(.4)+dy*np.sin(.4)
  yp=-dx*np.sin(.4)+dy*np.cos(.4)
  ring=np.sqrt((xp/.39)**2+(yp/.11)**2)
  mask=np.exp(-((ring-1)*85)**2)
  mask*=np.where((r<.245)&(yp<0),0,1)
  a=blend(a,np.array([220,231,219]),mask*.95)
 finish(a,slug,title,i+31)

# Portrait-like editorial silhouettes for original demo artists. These are
# intentionally illustrative: they do not depict real musicians.
artists={
 'luna-vale': ('Luna Vale',['#b9869b','#ecc7af','#332b43']),
 'north-coast': ('North Coast',['#2d5762','#88aaa2','#152b37']),
 'ellis-grey': ('Ellis Grey',['#d49066','#f0c792','#5e3939']),
 'the-velvets': ('The Velvets',['#637eaf','#bdc4de','#242c51']),
 'solana': ('Solana',['#ad7852','#edc383','#4c2d35']),
}
for i,(slug,(title,palette)) in enumerate(artists.items()):
 a=base(palette,i+100)
 # Lighting and indistinct profile contours create original cover portraits.
 head=((X-.51)/.19)**2+((Y-.37)/.23)**2
 shoulders=((X-.5)/.40)**2+((Y-.90)/.36)**2
 silhouette=np.maximum(np.clip((1-head)*14,0,1),np.clip((1-shoulders)*14,0,1))
 face=blend(color(palette[2])*.6,color(palette[1])*.72,np.clip(.82-X+Y*.1,0,1))
 a=blend(a,face,silhouette)
 # Hair crescent and broad soft highlight on the rim.
 hair=((X-.46)/.22)**2+((Y-.30)/.20)**2
 a=blend(a,color(palette[2])*.55,np.clip((1-hair)*18,0,1))
 finish(a,slug,title,i+100)

# Wide atmospheric hero image derived from the original Afterglow artwork.
hero=Image.open(OUT/'afterglow.png').resize((1600,1600),Image.Resampling.LANCZOS)
hero=hero.crop((0,240,1600,1040)).filter(ImageFilter.GaussianBlur(2))
hero.save(OUT/'afterglow-hero.png',optimize=True)
(OUT/'palettes.json').write_text(json.dumps({slug:palette for slug,(_,palette) in ART.items()},indent=2)+'\n')
# Contact sheet is a development artifact, intentionally outside app assets.
files=list(ART)+list(artists)
sheet=Image.new('RGB',(5*210,3*238),(18,17,21))
d=ImageDraw.Draw(sheet)
font=ImageFont.truetype(SANS,13)
for idx,slug in enumerate(files):
 thumb=Image.open(OUT/f'{slug}.png').resize((200,200),Image.Resampling.LANCZOS)
 xx=(idx%5)*210+5; yy=(idx//5)*238+5
 sheet.paste(thumb,(xx,yy)); d.text((xx,yy+205),slug,font=font,fill='white')
sheet.save('/tmp/danvu-artwork-contact.png')
print(f'Wrote {len(files)} original covers, hero and palettes to {OUT}')

# Original Music application icon: a crimson glass tile with a generic beamed
# musical note. No Apple or Spotify logos are used.
I=1024
iy,ix=np.mgrid[0:I,0:I].astype(float)/I
upper=np.array([255,83,115.0]); lower=np.array([202,25,65.0])
icon_rgb=upper[None,None,:]*(1-iy[...,None])+lower[None,None,:]*iy[...,None]
icon_rgb+=np.exp(-(((ix-.28)/.7)**2+((iy-.03)/.65)**2))[...,None]*np.array([0,11,12])
icon=Image.fromarray(np.uint8(np.clip(icon_rgb,0,255))).convert('RGBA')
rounded=Image.new('L',(I,I),0)
rd=ImageDraw.Draw(rounded)
rd.rounded_rectangle((28,28,I-28,I-28),radius=223,fill=255)
icon.putalpha(rounded)
# Clear, optically centered mark, drawn large enough to read at 16 pixels.
mark=Image.new('L',(I,I),0)
md=ImageDraw.Draw(mark)
md.polygon([(417,338),(714,265),(714,362),(468,423),(468,700),(417,700)],fill=255)
md.rectangle((663,321,714,641),fill=255)
# Elliptical noteheads with a slight upward tilt.
for cx,cy in [(397,700),(643,641)]:
 head=Image.new('L',(240,170),0)
 hd=ImageDraw.Draw(head)
 hd.ellipse((17,31,222,139),fill=255)
 head=head.rotate(18,Image.Resampling.BICUBIC,expand=True)
 mark.paste(head,(round(cx-head.width/2),round(cy-head.height/2)),head)
white=Image.new('RGBA',(I,I),(255,255,255,255))
white.putalpha(mark)
icon.alpha_composite(white)
# One-pixel inner material edge at the final 256px size.
edge=Image.new('RGBA',(I,I),(0,0,0,0)); ed=ImageDraw.Draw(edge)
ed.rounded_rectangle((31,31,I-31,I-31),radius=220,outline=(255,255,255,35),width=3)
icon.alpha_composite(edge)
assets=OUT.parent
icon256=icon.resize((256,256),Image.Resampling.LANCZOS)
icon256.save(assets/'music-icon.png',optimize=True)
icon.save(assets/'Music.ico',format='ICO',sizes=[(16,16),(32,32),(48,48),(64,64),(128,128),(256,256)])
print('Wrote Assets/Music.ico (16–256px) and Assets/music-icon.png (256px)')
