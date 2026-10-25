# gbOS86 image builder.
#
# This container has every tool the upstream gbos-x86 build expects, plus a
# small `docker` shim so build.sh's nested "docker run alpine ..." steps execute
# locally instead of needing a Docker socket inside the container.
FROM debian:trixie-slim

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      python3 \
      xz-utils \
      tar \
      coreutils \
      mtools \
      dosfstools \
      fdisk \
      e2fsprogs \
      busybox-static \
      sed \
      grep \
      gawk \
      findutils \
      file \
 && rm -rf /var/lib/apt/lists/*

COPY shim/ /opt/gbos86/shim/
COPY pack_image.py /opt/gbos86/pack_image.py
COPY build-in-container.sh /opt/gbos86/build-in-container.sh
RUN chmod 755 /opt/gbos86/shim/* /opt/gbos86/build-in-container.sh /opt/gbos86/pack_image.py

WORKDIR /src
