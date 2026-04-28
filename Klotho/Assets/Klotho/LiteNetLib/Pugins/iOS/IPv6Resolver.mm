#import <Foundation/Foundation.h>
#include <sys/socket.h>
#include <netdb.h>
#include <arpa/inet.h>

extern "C"
{
    /// <summary>
    /// Resolve IPv6 address on iOS.
    /// </summary>
    const char* _ResolveIPv6Address(const char* ipv4str)
    {
        struct addrinfo hints, *res = NULL;
        memset(&hints, 0, sizeof(hints));
        hints.ai_family   = AF_UNSPEC;
        hints.ai_socktype = SOCK_DGRAM;
        hints.ai_flags    = AI_DEFAULT;

        int err = getaddrinfo(ipv4str, NULL, &hints, &res);
        if (err != 0 || res == NULL) {
            return strdup(ipv4str); // fallback
        }

        char buf[INET6_ADDRSTRLEN];
        const char* result = ipv4str;

        if (res->ai_family == AF_INET6) {
            struct sockaddr_in6* addr6 = (struct sockaddr_in6*)res->ai_addr;
            if (inet_ntop(AF_INET6, &addr6->sin6_addr, buf, sizeof(buf))) {
                result = strdup(buf);
            }
        }

        freeaddrinfo(res);
        return result;
    }
}
