This project is supposed to represent best practices for the functional programming and be state-of-an-art for the DDD and Onion architecture (Ports/Adapters). Please strictly follow these rules:

Technology:
- Project is developed in F#, if you have issues understanding some syntax, you can try to find similarities mainly in Haskell / OCaml. If you are not sure about your proposal you can provide Haskell / OCaml version.

General:
- Always go for immutability if possible
- Avoid side effects unless it is IO (expressed as TaskResult), prefer Pure Functions
- Type-Driven Design: Make illegal states unrepresentable
- Use discriminated unions, avoid raw booleans
- Build complexity from small functions using pipe (|>) or composition (>>) operators
- Use TaskResult for IO operations, avoid Async/AsyncResult
- Always try to go for Railway-Oriented Programming with Result/TaskResult, avoid exceptions
- Follow best DDD/Onion architecture practices: keep domain layer pure, clean and following ubiquotus language
- Push effects/IO to the edges: infrastructure handles all side effects, but domain defines Ports
- Validate at boundaries: parse DTOs into domain types as early as possible
- Favor composition, avoid inheritance at all cost
- Follow single responsibility for smaller functions
- Follow pipeline thinking - data flows through transformations
- Use Property-Based Tests, create small generators for domain types and use them to generate particualar test cases; Avoid Gen.Filter in favor of manual mapping; Avoid fixed values
- Often use [<RequireQualifiedAccess>] for the modules to group the code
- Prefer using Monads as Computation Expressions (or even create new ones when you feel there is a need)

Essential Nuget libraries:
- FsToolkit.ErrorHandling - Railway-Oriented programming
- Expecto / FsCheck / FsCheck.Expecto - Property-based testing
- Giraffe / Falco - Web API Framework

Project structure:
- Services should have following layers:
  - Domain layer (Model, Ports, Workflows)
    - Pure functions
    - Strongly typed
    - No dependencies besides generally available libraries
    - Contains Model, Ports, Workflows
    - Workflows are the only way to interact with aggregates
  - Adapters (Infrastructure) layer
    - Concrete implementations like RabbitMQ/Postgres
  - API Layer
    - Functional Web Framework like Giraffe
    - Contains CompositionRoot that has all Ports->Adapters composition
- Always try to follow Railway-Oriented Programming (with Result type) and avoid exceptions