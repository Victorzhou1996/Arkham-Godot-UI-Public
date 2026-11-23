# 首页人物层重绘：右侧男人手里的手枪（Yunke 11-19 r9）

**问题**：首页人物层 `app/assets/ref/art/title-screen-v1/investigators.webp`（1671×941，透明底）里，右侧男人高举的东西像一根冒火的管子或火把，不像手枪。Yunke 原话："右侧男人拿着的是手枪，现在画的不太像"。

**做法**：在 GPT 图像编辑里，用原图做局部重绘（inpaint），只改他举起的右手和手里的枪；其余保持不变，包括人物、姿势、构图、透明背景和画风。不要整张重画，不然两个人会变样。

**交付**：同尺寸（1671×941）透明 PNG 或 WebP，放到 `inbox/art/title-cast-pistol-<日期>.png`。我会替换 `investigators.webp`（旧图留备份）。

---

## Prompt（英文，给 GPT 图像编辑）

> Inpaint only the raised right hand of the man in the fedora and trench coat (right figure). He holds a **1920s revolver** (a Colt-style .38 / .45 service revolver: visible cylinder, trigger guard, short barrel, wooden grip), pointed **straight up into the sky**, arm raised high, as if he has just fired a warning shot. A small, bright **muzzle flash** at the very tip of the barrel, plus a faint wisp of smoke. The gun must clearly read as a handgun at small size: dark gunmetal with a thin warm rim light along its top edge from the flash. His fingers wrap the grip naturally, with the index finger on the trigger.
>
> Keep everything else exactly as it is: the same painterly 1920s Lovecraftian noir style with flat, angular brush planes and a muted brown/olive palette; the same man (fedora, red tie, brown trench coat, waistcoat); the same woman beside him (red cloche hat, fur-collared coat, holding her revolver); the same low camera angle; the same lighting; and the **transparent background**. No torch, no flare, no tube, no fire coming from his hand; just the muzzle flash at the gun's tip. No text, no frame.

## 中文说明（同一意思）

> 只局部重绘右侧戴礼帽男人高举的右手：他握着一把 **1920 年代左轮手枪**（柯尔特式，能看到转轮、扳机护圈、短枪管和木握把），**枪口笔直朝天**，像刚鸣枪示警。枪口尖端有一小团明亮的枪口焰和一缕淡烟，枪身是深色金属，上沿被火光勾出一道暖色细边。手指自然握住枪柄，食指扣在扳机上。其余一切不变：画风、人物、女人和她的枪、仰拍角度、光线，以及透明背景。不要火把，不要冒火的管子，不要文字和边框。
