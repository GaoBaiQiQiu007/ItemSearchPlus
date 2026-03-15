物品查找增强版插件

该插件是基于 TShockAPI 开发的《泰拉瑞亚》服务器管理工具，专为服务器管理员设计，核心实现物品 / 箱子全维度管理 + 非法物品自动检测防护 两大核心能力，兼顾便捷性与服务器安全性：   
支持精准查询 / 删除玩家（在线 / 离线）、箱子中的指定物品，支持批量传送至玩家 / 箱子位置；   
自动定时扫描玩家全物品栏，检测非法物品（如天顶剑、最终棱镜）和异常堆叠物品，触发网住玩家、全服通报等处置动作；   
全服通报等处置动作；   
全指令化操作，无需修改配置文件即可完成所有管理动作，适配各类服务器管理场景。   

一、核心指令列表   
/si	/searchitem	itemsearch.cmd   查找服务器中持有指定物品的玩家及物品数量   
/sc	/searchchest	itemsearch.chest	   查找包含指定物品的箱子，显示箱子 ID / 坐标 / 数量    
/tpc	/tpchest	itemsearch.chesttp	   传送至指定 ID 的箱子位置（仅限游戏内使用）    
/ci	/chestinfo	itemsearch.chestinfo   	查询指定箱子的详细信息（坐标 / 名称 /    物品）    
/tpall	/tpall	itemsearch.tpall	   逐个传送至所有在线玩家位置（间隔 1 秒）   
/tpallc	/tpallchest	itemsearch.tpall	   逐个传送至所有箱子位置（间隔 0.3 秒）    
/rci	/removechestitem	itemsearch.rci   	删除指定箱子中的目标物品    
/ri	/removeitem	itemsearch.ri   	删除指定玩家的目标物品（在线 / 离线玩家均支持）    

1.2 指令使用示例    
查找持有 “天顶剑” 的玩家：/si 天顶剑 或 /si 757（天顶剑 ID）    
查找包含 “最终棱镜” 的箱子：/sc 最终棱镜    
传送至 ID 为 10 的箱子：/tpc 10    
删除玩家 “小明” 的 “金币枪”：/ri 小明 金币枪    
删除 ID 为 5 的箱子中的 “月亮领主符咒”：/rci 5 月亮领主符咒    
查看 ID 为 8 的箱子信息：/ci 8    

