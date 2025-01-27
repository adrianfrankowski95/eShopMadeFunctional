namespace eShop.Prelude

[<Struct>]
type Either<'left, 'right> =
    | Left of left: 'left
    | Right of right: 'right