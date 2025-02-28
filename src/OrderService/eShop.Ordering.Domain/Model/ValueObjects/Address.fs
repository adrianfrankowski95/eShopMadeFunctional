namespace eShop.Ordering.Domain.Model.ValueObjects

open eShop.ConstrainedTypes

type Street = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module Street =
    let create = String.NonWhiteSpace.create (nameof Street)

type City = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module City =
    let create = String.NonWhiteSpace.create (nameof City)

type State = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module State =
    let create = String.NonWhiteSpace.create (nameof State)

type Country = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module Country =
    let create = String.NonWhiteSpace.create (nameof Country)

type ZipCode = String.NonWhiteSpace

[<RequireQualifiedAccess>]
module ZipCode =
    let create = String.NonWhiteSpace.create (nameof ZipCode)

type Address =
    { Street: Street
      City: City
      State: State
      Country: Country
      ZipCode: ZipCode }
