# Copilot Instructions

## Project Guidelines
- Keep the tabular Q-Learning architecture using CustomHash<string, double[]>; improve training speed with parallel episode batches and evaluate/select the best learned policy rather than replacing it with a neural GPU model.
- Do not hardcode Q-Learning evaluation steps or target apples; use values entered by the user. The desired default target accuracy is 91.5%.