%global _libdir /usr/lib
%{!?_unitdir:%global _unitdir /usr/lib/systemd/system}
# This self-contained release has no separate debuginfo sources.
%global debug_package %{nil}
# .NET's optional LTTng trace provider is not required at runtime. The archive
# also ships Playwright's Node runtime; playwright.ps1 is Windows-only.
%global __requires_exclude ^(liblttng-ust[.]so[.]0.*|/usr/bin/(node|pwsh))$

Name:           chaptarrng
Version:        __VERSION__
Release:        1%{?dist}
Summary:        Standalone audiobook and ebook library manager
License:        GPL-3.0-only
URL:            https://github.com/snapetech/chaptarrng
Source0:        https://github.com/snapetech/chaptarrng/releases/download/v%{version}/ChaptarrNG-%{version}-linux-x64.tar.gz
Source1:        chaptarrng.service
Source2:        chaptarrng.sysusers
Source3:        chaptarrng.tmpfiles
Source4:        chaptarrng.package_info
BuildArch:      x86_64
Requires:       glibc
Requires:       libgcc
Requires:       libstdc++
Requires:       libicu
Requires:       openssl-libs
Requires:       sqlite-libs
Requires:       zlib
Requires(post): systemd
Requires(preun): systemd
Requires(postun): systemd

%description
ChaptarrNG is a standalone audiobook and ebook library manager with metadata,
library monitoring, release search, and download automation.

%prep
%setup -q -c -T
tar -xzf %{SOURCE0} --strip-components=1 -C .

%build

%install
install -d %{buildroot}/usr/lib/chaptarrng
install -d %{buildroot}%{_bindir}
install -d %{buildroot}%{_licensedir}/chaptarrng
install -d %{buildroot}%{_unitdir}
install -d %{buildroot}%{_prefix}/lib/sysusers.d
install -d %{buildroot}%{_prefix}/lib/tmpfiles.d
install -d %{buildroot}/usr/lib/chaptarrng/bin
cp -a ./. %{buildroot}/usr/lib/chaptarrng/bin/
install -m 0644 LICENSE %{buildroot}%{_licensedir}/chaptarrng/LICENSE
install -m 0644 %{_sourcedir}/chaptarrng.package_info %{buildroot}/usr/lib/chaptarrng/package_info
ln -s ../lib/chaptarrng/bin/Chaptarr %{buildroot}%{_bindir}/chaptarrng
install -m 0644 %{_sourcedir}/chaptarrng.service %{buildroot}%{_unitdir}/chaptarrng.service
install -m 0644 %{_sourcedir}/chaptarrng.sysusers %{buildroot}%{_prefix}/lib/sysusers.d/chaptarrng.conf
install -m 0644 %{_sourcedir}/chaptarrng.tmpfiles %{buildroot}%{_prefix}/lib/tmpfiles.d/chaptarrng.conf
chmod -R u=rwX,go=rX %{buildroot}/usr/lib/chaptarrng

%post
systemd-sysusers >/dev/null 2>&1 || :
systemd-tmpfiles --create /usr/lib/tmpfiles.d/chaptarrng.conf >/dev/null 2>&1 || :
systemctl daemon-reload >/dev/null 2>&1 || :

%preun
if [ "$1" -eq 0 ]; then
  systemctl disable --now chaptarrng.service >/dev/null 2>&1 || :
fi

%postun
systemctl daemon-reload >/dev/null 2>&1 || :

%files
%license %{_licensedir}/chaptarrng/LICENSE
%{_bindir}/chaptarrng
/usr/lib/chaptarrng/
%{_unitdir}/chaptarrng.service
%{_prefix}/lib/sysusers.d/chaptarrng.conf
%{_prefix}/lib/tmpfiles.d/chaptarrng.conf
