Shader "YC/Collection/RephasicOpticalPyramid"
{
    Properties
    {
        _OpticalCube ("中性棚拍反射环境", Cube) = "black" {}
        _IOR ("晶体折射率", Range(1.05,2.4)) = 1.52
        _CubeIntensity ("环境反射能量", Range(0,3)) = 0.95
        _Exposure ("光学显示曝光", Range(0.1,3)) = 1.18
        _PanelEnergy ("中性棚拍光卡能量", Range(0,8)) = 4.2
        _EnvironmentFloor ("中性环境底光", Range(0,1)) = 0.18
        _Absorption ("内部每米吸收系数", Vector) = (0.025,0.045,0.075,0)
    }
    SubShader
    {
        Tags { "Queue"="Geometry+5" "RenderType"="Opaque" }
        Pass
        {
            Name "FINITE_INTERIOR_OPTICS"
            Cull Back ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            // 各界面及内部镜片由同一 Blender 源脚本生成，不是表面图案。
            static const float4 opticalPlanes[4]={float4(0.00000000,-0.93806517,0.34645879,0.60283824),float4(0.81233871,0.46893877,0.34670204,0.60326152),float4(-0.81233871,0.46893877,0.34670207,0.60326158),float4(-0.00000000,0.00000000,-1.00000000,-0.23999999)};
static const float3 mirrorA[72]={float3(-0.15441725,-0.15561178,0.69869399),float3(0.30053243,-0.16658971,0.67992830),float3(-0.08690450,0.01753936,0.89267701),float3(-0.00008828,-0.07129197,1.49251449),float3(-0.18019959,-0.16856244,1.05165982),float3(0.04208795,-0.06730904,0.89676809),float3(-0.13064492,-0.27195349,0.94891411),float3(0.43040991,-0.26474738,0.34164631),float3(0.02559880,0.02146255,1.16817462),float3(0.09183941,-0.09597107,1.19820917),float3(-0.00878859,-0.17944837,1.13990533),float3(0.03469297,0.04823820,1.37283921),float3(-0.11912476,-0.30290723,0.39060402),float3(-0.12959987,-0.07913643,1.44775081),float3(-0.06976028,0.02370656,1.16264784),float3(-0.14530040,-0.04671903,0.89291102),float3(0.09766278,-0.09612831,1.40510929),float3(0.02908435,0.03041341,1.31229520),float3(0.40708357,-0.37448886,0.63457650),float3(0.06854236,-0.08698264,1.32655942),float3(-0.09229387,-0.03124603,0.63965636),float3(0.25852379,-0.23163946,0.96976459),float3(-0.13005476,-0.28716224,0.69850993),float3(-0.25420922,-0.08239618,0.45310360),float3(-0.11288212,-0.36166698,0.67389065),float3(-0.33073866,-0.11789578,0.92200315),float3(-0.12699629,-0.06103553,0.97703409),float3(-0.08285586,-0.11496035,0.78973705),float3(0.13064840,-0.06415097,1.46296573),float3(-0.01695799,-0.05768543,1.43351519),float3(0.44508582,-0.13009822,0.69468075),float3(0.04074703,-0.03935441,0.96800876),float3(0.15736540,-0.01007780,1.18651390),float3(0.08017927,-0.03880229,1.53439534),float3(-0.10264867,-0.19487034,1.13961530),float3(-0.02667986,0.01737519,0.66248184),float3(0.04960442,0.05623883,0.64512390),float3(0.35531616,-0.19860071,0.71156794),float3(0.11642718,-0.20214990,1.05963898),float3(0.08599141,-0.05336505,1.47883856),float3(0.08171710,-0.14984216,1.12101316),float3(0.01617787,0.07432468,1.20698619),float3(0.31688571,-0.16252166,1.13326550),float3(-0.36471194,0.30586630,0.34561846),float3(-0.01487043,0.18015417,1.28799021),float3(-0.09117061,0.04384793,0.76934141),float3(0.09852891,0.06702425,1.36101305),float3(0.05168680,-0.25749439,0.97858489),float3(0.31932363,-0.27669397,0.52692133),float3(-0.05312059,0.34240493,0.90495872),float3(0.07544488,0.10459460,1.35252404),float3(-0.13106875,-0.03703583,1.42997074),float3(0.14394972,-0.18748245,1.01143467),float3(0.49738097,-0.17094937,0.70837194),float3(-0.03220101,-0.04742208,1.48837852),float3(0.03532417,0.32658309,1.12836480),float3(-0.06590407,0.46677995,0.36454764),float3(0.16497833,0.19927557,0.99826235),float3(-0.25663379,-0.21161351,0.96629339),float3(0.19365764,0.04769861,1.14390230),float3(0.00035049,-0.12102690,1.35820138),float3(-0.02615660,0.08064893,1.49193120),float3(0.33169320,-0.24486622,0.54893261),float3(0.02760702,0.01312190,1.22946799),float3(-0.37890911,-0.36444736,0.39162678),float3(-0.53750682,-0.24841772,0.71831912),float3(0.17658553,-0.05477348,1.31830168),float3(0.09021936,-0.12277828,1.14529777),float3(-0.01168433,0.12468133,1.49526298),float3(0.06325229,-0.03299031,1.39486575),float3(-0.15250507,0.22461326,0.89312494),float3(0.06530484,-0.05245440,1.46951139)};
static const float3 mirrorB[72]={float3(-0.04844163,-0.27010977,0.57146370),float3(0.12876195,-0.14489903,0.62916112),float3(-0.18695325,-0.07387123,0.85952044),float3(0.00946755,-0.04310932,1.47866499),float3(-0.08905848,-0.06625903,0.97731066),float3(0.13437174,-0.03233931,0.84122574),float3(-0.18519807,-0.17372870,0.86652058),float3(0.22187424,-0.21430488,0.28103989),float3(-0.05919888,-0.06510399,1.11384356),float3(0.12301526,-0.14072597,1.17883503),float3(-0.05717128,-0.05087499,1.00884593),float3(0.12735580,0.00028357,1.33473921),float3(0.12316892,-0.43154034,0.47299340),float3(-0.03061464,0.01161763,1.38735199),float3(-0.14984791,-0.05535478,1.10278070),float3(-0.17540158,-0.25994360,0.74058795),float3(0.04027165,-0.01930965,1.36445272),float3(0.13634714,-0.04324828,1.24704957),float3(0.40563110,-0.11388545,0.52896428),float3(0.14216441,-0.07093692,1.26308072),float3(-0.00207267,-0.08050001,0.65743029),float3(0.04313597,-0.11653382,0.85434532),float3(-0.18345909,-0.08244882,0.59177959),float3(-0.27722093,0.01523737,0.37404567),float3(-0.13338161,-0.18931866,0.59461784),float3(-0.14005411,-0.14168799,0.80018967),float3(-0.21633211,-0.00660686,0.94300252),float3(-0.15954782,-0.33140495,0.71707779),float3(0.03992689,-0.00202263,1.40987158),float3(-0.01687208,0.04950204,1.37546206),float3(0.35249081,-0.34769776,0.49318942),float3(-0.09499539,-0.12807737,0.91640812),float3(0.05764619,0.02645282,1.13650572),float3(0.06751795,-0.05165251,1.52727079),float3(-0.13277146,-0.03805324,1.07281017),float3(-0.09966216,-0.02699924,0.60835135),float3(-0.09045656,-0.08699028,0.52006143),float3(0.37399969,-0.06580700,0.60326648),float3(0.13262574,-0.08656327,0.98898590),float3(0.09927747,-0.04126722,1.46348703),float3(-0.08487384,-0.21642779,1.06932604),float3(-0.06673522,0.10375237,1.18200314),float3(0.11038418,-0.14817162,1.01684022),float3(-0.18488242,0.29463556,0.27633470),float3(-0.10446383,0.09009965,1.25979185),float3(-0.13806883,-0.23278949,0.65993005),float3(0.08357149,0.06979359,1.35262144),float3(0.05977726,-0.17862353,0.95733052),float3(0.56094939,-0.35427499,0.51191783),float3(-0.12119001,0.31367308,0.85173064),float3(0.01025266,0.18817462,1.30449605),float3(-0.02351761,-0.02944362,1.39568090),float3(0.20154950,-0.19016315,0.98461449),float3(0.40964341,-0.25244284,0.63129717),float3(-0.03217612,0.00803826,1.45145226),float3(-0.04538442,0.27094281,1.08991480),float3(-0.08992878,0.36008325,0.32828796),float3(0.04690642,0.13051228,1.01631773),float3(-0.35814759,-0.11917377,0.90016407),float3(0.12164115,0.00298462,1.05303299),float3(-0.04737882,-0.10018249,1.34033048),float3(0.08211675,-0.02926479,1.41706586),float3(0.40706700,-0.28542078,0.48534766),float3(0.19113010,-0.05696826,1.17688274),float3(-0.26137739,-0.25070962,0.29846036),float3(-0.45879331,-0.18590230,0.65827972),float3(0.08396893,-0.12499434,1.30425549),float3(0.23112506,-0.07506853,1.05168486),float3(-0.00364445,0.12467525,1.49006760),float3(0.09676240,-0.10878405,1.36370289),float3(0.02361093,0.13811742,0.81006813),float3(0.10483228,-0.08279391,1.45681059)};
static const float3 mirrorC[72]={float3(-0.14369355,-0.31461117,0.77374607),float3(0.16133142,-0.15185338,0.78886372),float3(-0.21658704,-0.02338950,0.97575378),float3(0.01393776,-0.05223181,1.50750947),float3(-0.11047200,-0.01092905,1.16738045),float3(0.10818286,0.02144475,0.95856899),float3(-0.12437044,-0.16131414,0.98650676),float3(0.28172681,-0.13228095,0.47083712),float3(-0.08601974,-0.01869114,1.22620428),float3(0.10175790,-0.15967405,1.22789252),float3(-0.08255127,-0.03928730,1.18282294),float3(0.07101535,-0.05128448,1.44188738),float3(0.07157157,-0.12480982,0.50773442),float3(-0.03998429,-0.07420769,1.48089302),float3(-0.19096324,-0.02156924,1.20295703),float3(-0.20439540,-0.25297001,0.98239714),float3(0.01232533,-0.02782016,1.44817913),float3(0.07097699,-0.05419974,1.34762549),float3(0.28980076,-0.13123268,0.79620016),float3(0.14676714,-0.02205016,1.35589933),float3(-0.02019730,0.03748585,0.67966318),float3(0.02802522,-0.22385578,1.06507134),float3(-0.07194489,-0.09531937,0.75639457),float3(-0.29548961,-0.02724646,0.49043465),float3(-0.03430081,-0.22829977,0.70355904),float3(-0.11866423,-0.11410479,0.96428972),float3(-0.14050034,0.02171965,1.01751268),float3(-0.31524211,-0.21096763,0.90900558),float3(0.01394929,-0.05173030,1.49930108),float3(0.06955650,-0.00440478,1.49287653),float3(0.21009028,-0.27586406,0.82348424),float3(-0.09655743,-0.15134485,1.05690527),float3(0.07373156,-0.04910504,1.22915220),float3(0.06509945,-0.05084728,1.54666805),float3(0.00960896,-0.06960841,1.21102047),float3(-0.06914774,-0.08274908,0.72024578),float3(-0.09572403,-0.12845275,0.74596083),float3(0.38525152,-0.07729865,0.80770248),float3(0.10125225,-0.08695891,1.08063352),float3(0.10509235,-0.04407065,1.49339294),float3(-0.10581480,-0.08221509,1.22402632),float3(-0.06689876,0.03855054,1.25843525),float3(0.13779949,-0.12708466,1.26498377),float3(-0.23046631,0.36759183,0.42924631),float3(-0.01909412,0.04896479,1.34715748),float3(0.00528421,-0.16201542,0.92903090),float3(0.08440123,0.06714606,1.37318635),float3(0.10732402,-0.20163058,1.01375639),float3(0.51054978,-0.13813037,0.61564994),float3(-0.12242354,0.33205104,0.94715565),float3(-0.02578393,0.10555439,1.38905442),float3(-0.00563560,-0.05225381,1.49191797),float3(0.21486410,-0.23369052,1.04872954),float3(0.41322470,-0.25176331,0.77686125),float3(-0.07676102,-0.02158781,1.52452970),float3(0.00711860,0.24618512,1.17284226),float3(-0.17403008,0.39890045,0.39769655),float3(0.07379388,0.23281617,1.03124690),float3(-0.36772323,-0.15228242,1.00830269),float3(0.08555733,0.03183154,1.18311441),float3(-0.01786174,-0.08736593,1.37449622),float3(-0.01495207,-0.03892716,1.52428126),float3(0.40636864,-0.28369468,0.62070274),float3(0.12101442,-0.14100887,1.29638600),float3(-0.32004943,-0.22810906,0.46226409),float3(-0.44432685,-0.24463522,0.77424586),float3(0.10527004,-0.02905445,1.31405425),float3(0.21582298,-0.11950151,1.21680784),float3(-0.00429487,0.12358069,1.49796653),float3(0.13273099,-0.06078297,1.43229926),float3(-0.09459350,0.07970246,0.97727323),float3(0.11512093,-0.05986753,1.48754179)};
static const float3 mirrorTint[72]={float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.97740193,0.78741229,0.38005634),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.97740193,0.78741229,0.38005634),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.97740193,0.78741229,0.38005634),float3(0.91140750,0.55422176,0.09462963),float3(0.28464669,0.11928019,0.02198095),float3(0.97740193,0.78741229,0.38005634),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.97740193,0.78741229,0.38005634),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.91140750,0.55422176,0.09462963),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.91140750,0.55422176,0.09462963),float3(0.97740193,0.78741229,0.38005634),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939),float3(0.86889939,0.89000541,0.86889939)};


            samplerCUBE _OpticalCube;
            float _IOR, _CubeIntensity, _Exposure, _PanelEnergy, _EnvironmentFloor;
            float4 _Absorption;

            struct inputData
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 opticalXY : TEXCOORD1;
                float2 opticalZFace : TEXCOORD2;
            };
            struct pixelData
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 source : TEXCOORD2;
                float face : TEXCOORD3;
            };
            pixelData vert(inputData v)
            {
                pixelData o;
                o.pos=UnityObjectToClipPos(v.vertex);
                o.world=mul(unity_ObjectToWorld,v.vertex).xyz;
                o.normal=UnityObjectToWorldNormal(v.normal);
                o.source=float3(v.opticalXY,v.opticalZFace.x);
                o.face=v.opticalZFace.y;
                return o;
            }

            // 使用表面导数和真实法线重建源坐标/世界坐标映射。
            // UV 中的源坐标不受 Blender FBX 轴转换、Unity 层级旋转与缩放影响。
            float3 sourceToWorld(float3 direction,float3 sa,float3 sb,float3 wa,float3 wb,float3 sn,float3 wn,float scale)
            {
                float aa=dot(sa,sa),ab=dot(sa,sb),bb=dot(sb,sb);
                float denominator=max(aa*bb-ab*ab,1e-16);
                float u=(dot(direction,sa)*bb-dot(direction,sb)*ab)/denominator;
                float v=(dot(direction,sb)*aa-dot(direction,sa)*ab)/denominator;
                return normalize(u*wa+v*wb+dot(direction,sn)*wn*scale);
            }
            float3 worldToSource(float3 direction,float3 sa,float3 sb,float3 wa,float3 wb,float3 sn,float3 wn,float scale)
            {
                float aa=dot(wa,wa),ab=dot(wa,wb),bb=dot(wb,wb);
                float denominator=max(aa*bb-ab*ab,1e-16);
                float u=(dot(direction,wa)*bb-dot(direction,wb)*ab)/denominator;
                float v=(dot(direction,wb)*aa-dot(direction,wa)*ab)/denominator;
                return normalize(u*sa+v*sb+dot(direction,wn)*sn/max(scale,1e-5));
            }
            float triangleDistance(float3 origin,float3 direction,int index)
            {
                float3 edge1=mirrorB[index]-mirrorA[index];
                float3 edge2=mirrorC[index]-mirrorA[index];
                float3 h=cross(direction,edge2);
                float determinant=dot(edge1,h);
                if(abs(determinant)<1e-7) return 1e5;
                float inverseDeterminant=1/determinant;
                float3 offset=origin-mirrorA[index];
                float u=dot(offset,h)*inverseDeterminant;
                if(u<0 || u>1) return 1e5;
                float3 q=cross(offset,edge1);
                float v=dot(direction,q)*inverseDeterminant;
                if(v<0 || u+v>1) return 1e5;
                float distance=dot(edge2,q)*inverseDeterminant;
                return distance>.0007?distance:1e5;
            }
            float fresnel(float cosine)
            {
                float r=(_IOR-1)/(_IOR+1);
                float r0=r*r;
                return r0+(1-r0)*pow(1-saturate(cosine),5);
            }
            float rectangularLightCard(float3 ray,float3 axis,float2 halfSize)
            {
                axis=normalize(axis);
                float cosine=dot(ray,axis);
                if(cosine<=.001) return 0;
                float3 horizontal=normalize(cross(float3(0,1,0),axis));
                float3 vertical=cross(axis,horizontal);
                float2 coordinate=abs(float2(dot(ray,horizontal),dot(ray,vertical))/cosine);
                float2 coverage=1-smoothstep(halfSize-.015,halfSize+.015,coordinate);
                return coverage.x*coverage.y;
            }
            float3 environment(float3 direction,float3 sa,float3 sb,float3 wa,float3 wb,float3 sn,float3 wn,float scale)
            {
                float3 worldDirection=sourceToWorld(direction,sa,sb,wa,wb,sn,wn,scale);
                float3 backdrop=max(texCUBElod(_OpticalCube,float4(worldDirection,0)).rgb,0)*_EnvironmentFloor;
                // 真实方向上的中性矩形光卡，代表棚拍入射光源的有限角面积。
                // 它们固定在世界环境，壳体旋转时镜像随路径移动；无表面或屏幕图案。
                float cardA=rectangularLightCard(worldDirection,float3(-.55,.75,-.35),float2(.65,.42));
                float cardB=rectangularLightCard(worldDirection,float3(.72,.28,-.55),float2(.45,.78));
                float cardC=rectangularLightCard(worldDirection,float3(.22,.65,.73),float2(.68,.45));
                // 下方和背侧的大面积反射板给多次折转后的路径提供入射光。
                // 板固定在世界环境；颜色通过真实内部路径和镜片反射率进入视线。
                float warmReflector=rectangularLightCard(worldDirection,float3(-.18,-.65,-.72),float2(1.9,1.6));
                float silverReflector=rectangularLightCard(worldDirection,float3(-.8,-.12,.6),float2(1.7,1.6));
                float3 broadReflections=warmReflector*float3(1,.83,.55)*.32
                    +silverReflector*float3(.95,.98,1)*.30;
                return (backdrop+_PanelEnergy*(cardA+cardB*.85+cardC*.92+broadReflections))*_CubeIntensity;
            }
            float4 frag(pixelData i) : SV_Target
            {
                float3 sa=ddx(i.source),sb=ddy(i.source),wa=ddx(i.world),wb=ddy(i.world);
                int faceIndex=clamp((int)round(i.face),0,3);
                float3 sn=opticalPlanes[faceIndex].xyz,wn=normalize(i.normal);
                float scale=sqrt((dot(wa,wa)+dot(wb,wb))/max(dot(sa,sa)+dot(sb,sb),1e-12));
                float3 towardCamera=normalize(lerp(_WorldSpaceCameraPos-i.world,UNITY_MATRIX_V[2].xyz,unity_OrthoParams.w));
                float3 incoming=worldToSource(-towardCamera,sa,sb,wa,wb,sn,wn,scale);
                float entryFresnel=fresnel(-dot(incoming,sn));
                float3 radiance=environment(reflect(incoming,sn),sa,sb,wa,wb,sn,wn,scale)*entryFresnel;
                float3 direction=normalize(refract(incoming,sn,1/_IOR));
                float3 origin=i.source+direction*.001;
                float3 throughput=1-entryFresnel;

                // 每一步优先求真实有限内部三角镜片和四面边界中的最近交点。
                // 面内全反射会继续行进；可出射时按 Fresnel 分出出口辐射，剩余反射继续。
                [loop] for(int bounce=0;bounce<10;bounce++)
                {
                    float distance=1e5;
                    int hitFace=-1,hitMirror=-1;
                    [unroll] for(int face=0;face<4;face++)
                    {
                        float denominator=dot(direction,opticalPlanes[face].xyz);
                        if(denominator>.00001)
                        {
                            float candidate=(opticalPlanes[face].w-dot(origin,opticalPlanes[face].xyz))/denominator;
                            if(candidate>.0005 && candidate<distance)
                            {distance=candidate;hitFace=face;}
                        }
                    }
                    [loop] for(int mirror=0;mirror<72;mirror++)
                    {
                        float candidate=triangleDistance(origin,direction,mirror);
                        if(candidate<distance)
                        {distance=candidate;hitMirror=mirror;hitFace=-1;}
                    }
                    if(distance>1000) break;
                    throughput*=exp(-distance*_Absorption.rgb);
                    origin+=direction*distance;
                    if(hitMirror>=0)
                    {
                        float3 n=normalize(cross(mirrorB[hitMirror]-mirrorA[hitMirror],mirrorC[hitMirror]-mirrorA[hitMirror]));
                        float grazing=pow(1-saturate(abs(dot(direction,n))),5);
                        direction=reflect(direction,n);
                        // 金属掠射 Fresnel 增强反射；金银色仍来自内部实体镜片。
                        throughput*=lerp(mirrorTint[hitMirror],float3(1,1,1),grazing)*.97;
                    }
                    else
                    {
                        float3 n=opticalPlanes[hitFace].xyz;
                        float3 outgoing=refract(direction,-n,_IOR);
                        bool totalInternalReflection=dot(outgoing,outgoing)<.000001;
                        float f=totalInternalReflection?1:fresnel(dot(direction,n));
                        if(!totalInternalReflection)
                            radiance+=throughput*(1-f)*environment(normalize(outgoing),sa,sb,wa,wb,sn,wn,scale);
                        throughput*=f;
                        direction=reflect(direction,n);
                    }
                    origin+=direction*.001;
                    if(max(throughput.r,max(throughput.g,throughput.b))<.0001) break;
                }
                // 十跳截断处用剩余方向的环境闭合路径，保留尚未被吸收的能量。
                // 这里仍是有限路径近似，不将全反射剩余能量额外削减为 16%。
                radiance+=throughput*environment(direction,sa,sb,wa,wb,sn,wn,scale);
                return float4(1-exp(-max(radiance,0)*_Exposure),1);
            }
            ENDCG
        }
        Pass
        {
            Name "SHADOWCASTER"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual Cull Back
            CGPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            struct shadowData {V2F_SHADOW_CASTER;};
            shadowData shadowVert(appdata_base v){shadowData o;TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);return o;}
            float4 shadowFrag(shadowData i):SV_Target{SHADOW_CASTER_FRAGMENT(i)}
            ENDCG
        }
    }
}
