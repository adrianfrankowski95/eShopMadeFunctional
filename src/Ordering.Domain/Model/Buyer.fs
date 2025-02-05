namespace Ordering.Domain.Model

open eShop.ConstrainedTypes
open eShop.DomainDrivenDesign

type Buyer =
    { Name: NonWhiteSpaceString; PaymentMethods: PaymentMethod Set }