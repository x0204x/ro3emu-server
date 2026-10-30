# Equipment grids

`31400 EquipItemOpr` places equipment using `ItemConfig._iEquipPart`; the selected grid is persisted in the active
equipment plan and is also used by plan switching and weapon-card sockets.

A one-handed weapon (`ItemConfig._iEquipPart = 2`, MainHand) may use both weapon grids when the career wields one in
each hand. The client decides it from `JobConfig._kWeaponForm` (`LC_EquipmentHelper.GetWeaponStatusType`, enum_WeaponFromType
SingleHand 1, TwoHand 2, DoubleHand 3): a career without SingleHand but with DoubleHand is `TwoHandAndDoubleHand` and
puts a MainHand weapon in the off hand once the main hand is taken. Today that is the Assassin line, Assassin `5200`,
Assassin Cross `5300` and Guillotine Cross `5400` (`[2,3]`). The client asks for no skill, so neither does the server.

Equipment-plan validation and weapon-card sockets use the same second-hand rule, so saved plans and cards remain valid
when an Assassin equips a dagger in each hand.
