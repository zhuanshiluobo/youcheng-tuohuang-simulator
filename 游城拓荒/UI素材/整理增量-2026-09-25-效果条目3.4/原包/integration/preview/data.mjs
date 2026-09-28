export const DATA = {
  palette:{green:'#5FD200',yellow:'#FFBF00',blue:'#006CFF',red:'#EC0000'},
  round:4,roundCount:8,playerCount:4,phase:'行动阶段',redZoneRounds:{2:6,3:5,4:4},
  players:[
    {name:'蓝方',color:'blue',mask:4,portrait:0,score:12,res:[5,3,2,1],vouchers:18,available:8,effect:'异客 · 隐藏仓库',markers:3,note:'支付时可用'},
    {name:'黄方',color:'yellow',mask:2,portrait:1,score:8,res:[4,1,3,0],vouchers:12,available:6,effect:'多萝西',markers:2,note:'本次收尾未用'},
    {name:'绿方',color:'green',mask:1,portrait:4,score:12,res:[4,3,2,1],vouchers:14,available:6,effect:'暂无留场效果',markers:0,note:''}
  ],
  companies:[['罗德岛',[2,0,5,8,0,0]],['海德兄弟',[8,4,1,0,2,0]],['莱茵生命',[4,10,0,1,0,0]],['黑钢国际',[2,0,9,0,4,0]]],
  characterNames:['雷蛇','德克萨斯','锡人','坎诺特','极境','玛恩纳','山'],
  portraitCrops:[[175,92,350,373],[190,92,335,378],[145,92,350,378],[120,92,365,378],[105,92,360,378],[95,92,365,378],[155,92,360,365]],
  actions:['部署','调度','探索','城市移动','建设','特殊行动'],
  quickActions:[['使用角色效果','查看当前可执行的角色效果'],['企业家效果','查看本阶段可用的企业家效果'],['其他快速行动','具体选项由游戏状态提供']],
  patterns:['军工化区域','动员配套体系','复合动力系统','核心商业区','源石工业中枢','警备队辖区']
};
export const GEOMETRY={
 screen:[0,0,1920,1080],topbar:[0,0,1920,64],opponents:[[16,72,416,108],[16,188,416,108],[16,304,416,108]],
 city:[16,424,416,554],map:[444,72,896,670],self:[444,750,896,48],entrepreneurs:[444,806,424,172],hand:[880,806,460,172],
 cooperation:[1352,72,552,282],discard:[1352,366,270,220],covered:[1634,366,270,220],tabs:[[1352,598,180,48],[1538,598,180,48],[1724,598,180,48]],page:[1352,654,552,324],
 bottom:[16,986,1888,82],undo:[36,1005,132,48],chain:[188,999,1424,60],end:[1640,1003,236,52],drawer:[444,746,1448,228]
};
export const INITIAL_STATE={mode:'idle',tab:'actions',collapsed:false,drawerOpen:false,undoEnabled:false,optionalEffect:null,expandedPlayer:null,detailScroll:0,quickScroll:0,effectRowStatus:{}};
