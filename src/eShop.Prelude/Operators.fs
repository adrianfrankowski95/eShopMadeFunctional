module eShop.Prelude.Operators

let inline (>>>) (f: 'a -> 'b -> 'c) (g: 'c -> 'd) = fun a -> f a >> g
let inline (>>>>) (f: 'a -> 'b -> 'c -> 'd) (g: 'd -> 'e) = fun a -> f a >>> g
let inline (>=>) f g = f >> Result.bind g