FROM mcr.microsoft.com/dotnet/sdk:10.0

# Install dependencies required to run Avalonia UI and X11 on Linux
RUN apt-get update && export DEBIAN_FRONTEND=noninteractive \
    && apt-get -y install --no-install-recommends \
    libx11-dev \
    libice-dev \
    libsm-dev \
    libfontconfig1-dev \
    libfreetype6-dev \
    libgl1-mesa-dev \
    x11-apps \
    && apt-get clean -y && rm -rf /var/lib/apt/lists/*

# Install Avalonia templates for dotnet new
RUN dotnet new install Avalonia.Templates
