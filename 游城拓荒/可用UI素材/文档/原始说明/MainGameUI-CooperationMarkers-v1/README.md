# 企业合作玩家标记 v1

包含15种非空组合与一个空槽，全部提供SVG以及32、64、128像素透明PNG喵

Green = #5FD200，Yellow = #FFBF00，Blue = #006CFF，Red = #EC0000 喵

位值为G=1、Y=2、B=4、R=8，组合取按位或，文件编号采用两位十六进制，例如绿蓝为05，使用04-markers/cooperation-05-64.png喵

两色对角平分、三色等宽竖分、四色固定为左上G、右上Y、左下B、右下R喵

overlay-hover、overlay-selected、overlay-focus是独立透明状态叠层，不覆盖内部玩家色喵

digit-base-green/yellow/blue/red用于叠加独立数字，素材本身不包含数字喵

完整映射和几何信息见04-markers/markers.json，预览见04-markers/gallery.png喵

颜色原值已在PNG内部像素校验，不应对组合标记进行整体着色或调色喵
