import React, { useState, useEffect } from "react";
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  Image,
  ScrollView,
  SafeAreaView,
  KeyboardAvoidingView,
  Platform,
  Dimensions,
  StatusBar,
  ActivityIndicator,
  Pressable,
} from "react-native";
import { LinearGradient } from "expo-linear-gradient";
import { AntDesign, FontAwesome, Ionicons, MaterialCommunityIcons } from "@expo/vector-icons";
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  withTiming,
  interpolate,
  withSequence,
  withDelay,
} from "react-native-reanimated";
import { router } from "expo-router";
import { useAuth } from "@/context/AuthContext";
import { authService } from "@/services/auth.service";
import { ErrorMessage } from "@/components/ErrorMessage";
import { useFormAnimation } from "@/hooks/use-form-animation";

const { width, height } = Dimensions.get("window");

export default function AuthScreen() {
  const [isSignup, setIsSignup] = useState(false);
  const [isForgot, setIsForgot] = useState(false);
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMsg, setSuccessMsg] = useState<string | null>(null);
  const [isEmailFocused, setIsEmailFocused] = useState(false);
  const [isPasswordFocused, setIsPasswordFocused] = useState(false);
  const [isNameFocused, setIsNameFocused] = useState(false);
  
  const { signIn, signUp, isAuthenticated } = useAuth();
  const { shake, shakeStyle } = useFormAnimation();
  const animation = useSharedValue(0);
  const contentOpacity = useSharedValue(0);
  const headerTranslateY = useSharedValue(-50);

  useEffect(() => {
    if (isAuthenticated) {
      router.replace("/(tabs)/map");
    }
    contentOpacity.value = withDelay(300, withTiming(1, { duration: 800 }));
    headerTranslateY.value = withSpring(0, { damping: 15 });
  }, [isAuthenticated]);

  useEffect(() => {
    animation.value = withSpring(isSignup ? 1 : 0, {
      damping: 20,
      stiffness: 90,
    });
    setError(null);
    setSuccessMsg(null);
  }, [isSignup, animation]);

  const handleSubmit = async () => {
    setError(null);
    setSuccessMsg(null);

    if (isForgot) {
      if (!email) {
        setError("Please enter your email address");
        shake();
        return;
      }
      setIsSubmitting(true);
      try {
        await authService.forgotPassword(email);
        setSuccessMsg("If your email is registered, you will receive a reset token.");
      } catch (err: any) {
        setError(err.message || "Something went wrong. Please try again.");
      } finally {
        setIsSubmitting(false);
      }
      return;
    }

    if (!email || !password || (isSignup && !fullName)) {
      setError("Please fill in all mandatory fields");
      shake();
      return;
    }

    setIsSubmitting(true);
    try {
      if (isSignup) {
        await signUp(email, password, fullName);
      } else {
        await signIn(email, password);
      }
      router.replace("/(tabs)/map");
    } catch (err: any) {
      setError(err.message || "Failed to authenticate. Please try again.");
      shake();
    } finally {
      setIsSubmitting(false);
    }
  };

  const animatedSwitchStyle = useAnimatedStyle(() => {
    // Total horizontal padding: Outer(20*2) + Card(24*2) + TabContainer(4*2) = 88 + 8 = 96
    const availableWidth = width - 88 - 8;
    const tabWidth = availableWidth / 2;
    const translateX = interpolate(animation.value, [0, 1], [4, tabWidth + 4]);
    return {
      transform: [{ translateX }],
    };
  });

  const animatedHeaderStyle = useAnimatedStyle(() => {
    return {
      opacity: contentOpacity.value,
      transform: [{ translateY: headerTranslateY.value }],
    };
  });

  const animatedCardStyle = useAnimatedStyle(() => {
    return {
      opacity: contentOpacity.value,
      transform: [{ translateY: interpolate(contentOpacity.value, [0, 1], [20, 0]) }],
    };
  });

  return (
    <View style={styles.mainContainer}>
      <StatusBar barStyle="light-content" />
      <LinearGradient
        colors={["#0F172A", "#111827"]}
        style={StyleSheet.absoluteFill}
      />
      
      {/* Subtle Background Glow */}
      <View style={[styles.blurCircle, { top: -100, right: -100, backgroundColor: "#3B82F615", width: 400, height: 400 }]} />

      <SafeAreaView style={styles.safeArea}>
        <KeyboardAvoidingView
          behavior={Platform.OS === "ios" ? "padding" : undefined}
          style={{ flex: 1 }}
        >
          <ScrollView
            contentContainerStyle={styles.scrollContent}
            showsVerticalScrollIndicator={false}
            keyboardShouldPersistTaps="handled"
          >
            <Animated.View style={[styles.header, animatedHeaderStyle]}>
              <View style={styles.logoContainer}>
                <Image
                  source={require("../assets/images/logo.png")}
                  style={styles.logo}
                  resizeMode="contain"
                />
                <View style={styles.logoGlow} />
              </View>
              <Text style={styles.title}>
                Access<Text style={styles.cityText}>City</Text>
              </Text>
              <Text style={styles.subtitle}>
                Navigate your world with confidence
              </Text>
            </Animated.View>

            <Animated.View style={[styles.authCard, animatedCardStyle, shakeStyle]}>
              {!isForgot ? (
                <>
                  <View style={styles.tabContainer}>
                    <Animated.View style={[styles.tabSlider, animatedSwitchStyle]} />
                    <Pressable
                      style={styles.tab}
                      onPress={() => setIsSignup(false)}
                    >
                      <Text style={[styles.tabText, !isSignup && styles.tabTextActive]}>Log In</Text>
                    </Pressable>
                    <Pressable
                      style={styles.tab}
                      onPress={() => setIsSignup(true)}
                    >
                      <Text style={[styles.tabText, isSignup && styles.tabTextActive]}>Sign Up</Text>
                    </Pressable>
                  </View>

                  <View style={styles.form}>
                    {isSignup && (
                      <View style={[styles.inputWrapper, isNameFocused && styles.inputWrapperFocused]}>
                        <Ionicons name="person-outline" size={20} color={isNameFocused ? "#3B82F6" : "#64748B"} />
                        <TextInput
                          placeholder="Full Name"
                          placeholderTextColor="#64748B"
                          style={styles.input}
                          value={fullName}
                          onChangeText={setFullName}
                          onFocus={() => setIsNameFocused(true)}
                          onBlur={() => setIsNameFocused(false)}
                        />
                      </View>
                    )}

                    <View style={[styles.inputWrapper, isEmailFocused && styles.inputWrapperFocused]}>
                      <Ionicons name="mail-outline" size={20} color={isEmailFocused ? "#3B82F6" : "#64748B"} />
                      <TextInput
                        placeholder="Email Address"
                        placeholderTextColor="#64748B"
                        keyboardType="email-address"
                        autoCapitalize="none"
                        style={styles.input}
                        value={email}
                        onChangeText={setEmail}
                        onFocus={() => setIsEmailFocused(true)}
                        onBlur={() => setIsEmailFocused(false)}
                      />
                    </View>

                    <View style={[styles.inputWrapper, isPasswordFocused && styles.inputWrapperFocused]}>
                      <Ionicons name="lock-closed-outline" size={20} color={isPasswordFocused ? "#3B82F6" : "#64748B"} />
                      <TextInput
                        placeholder="Password"
                        placeholderTextColor="#64748B"
                        secureTextEntry
                        style={styles.input}
                        value={password}
                        onChangeText={setPassword}
                        onFocus={() => setIsPasswordFocused(true)}
                        onBlur={() => setIsPasswordFocused(false)}
                      />
                    </View>

                    <ErrorMessage visible={!!error} message={error ?? undefined} />

                    <TouchableOpacity 
                      onPress={() => {
                        setIsForgot(true);
                        setError(null);
                        setSuccessMsg(null);
                      }}
                      style={styles.forgotBtn}
                    >
                      <Text style={styles.forgotText}>Forgot Password?</Text>
                    </TouchableOpacity>

                    <TouchableOpacity 
                      activeOpacity={0.8} 
                      onPress={handleSubmit}
                      disabled={isSubmitting}
                      style={styles.mainBtnContainer}
                    >
                      <LinearGradient
                        colors={["#3B82F6", "#2563EB"]}
                        start={{ x: 0, y: 0 }}
                        end={{ x: 1, y: 0 }}
                        style={styles.mainBtn}
                      >
                        {isSubmitting ? (
                          <ActivityIndicator color="#FFF" />
                        ) : (
                          <>
                            <Text style={styles.mainBtnText}>
                              {isSignup ? "Create Account" : "Sign In"}
                            </Text>
                            <Ionicons name="arrow-forward" size={18} color="#FFF" style={{ marginLeft: 6 }} />
                          </>
                        )}
                      </LinearGradient>
                    </TouchableOpacity>

                    <View style={styles.divider}>
                      <View style={styles.line} />
                      <Text style={styles.dividerText}>OR</Text>
                      <View style={styles.line} />
                    </View>

                    <View style={styles.socialRow}>
                      <TouchableOpacity style={styles.socialBtn}>
                        <AntDesign name="google" size={22} color="#FFF" />
                      </TouchableOpacity>
                      <TouchableOpacity style={styles.socialBtn}>
                        <FontAwesome name="apple" size={24} color="#FFF" />
                      </TouchableOpacity>
                    </View>
                  </View>
                </>
              ) : (
                <View style={styles.form}>
                  <Text style={styles.forgotHeaderTitle}>Reset Password</Text>
                  <Text style={styles.forgotSubtitle}>Enter your email address and we'll send you a token to reset your password.</Text>
                  
                  <View style={[styles.inputWrapper, isEmailFocused && styles.inputWrapperFocused]}>
                    <Ionicons name="mail-outline" size={20} color={isEmailFocused ? "#3B82F6" : "#64748B"} />
                    <TextInput
                      placeholder="Email Address"
                      placeholderTextColor="#64748B"
                      keyboardType="email-address"
                      autoCapitalize="none"
                      style={styles.input}
                      value={email}
                      onChangeText={setEmail}
                      onFocus={() => setIsEmailFocused(true)}
                      onBlur={() => setIsEmailFocused(false)}
                    />
                  </View>

                  {successMsg && (
                    <View style={styles.successContainer}>
                      <Ionicons name="checkmark-circle" size={18} color="#10B981" />
                      <Text style={styles.successText}>{successMsg}</Text>
                    </View>
                  )}

                  <ErrorMessage visible={!!error} message={error ?? undefined} />

                  <TouchableOpacity 
                    activeOpacity={0.8} 
                    onPress={handleSubmit}
                    disabled={isSubmitting}
                    style={styles.mainBtnContainer}
                  >
                    <LinearGradient
                      colors={["#3B82F6", "#2563EB"]}
                      start={{ x: 0, y: 0 }}
                      end={{ x: 1, y: 0 }}
                      style={styles.mainBtn}
                    >
                      {isSubmitting ? (
                        <ActivityIndicator color="#FFF" />
                      ) : (
                        <Text style={styles.mainBtnText}>Send Reset Link</Text>
                      )}
                    </LinearGradient>
                  </TouchableOpacity>

                  <TouchableOpacity 
                    onPress={() => {
                      setIsForgot(false);
                      setError(null);
                      setSuccessMsg(null);
                    }}
                    style={styles.backBtn}
                  >
                    <Ionicons name="arrow-back" size={16} color="#64748B" />
                    <Text style={styles.backBtnText}>Back to Login</Text>
                  </TouchableOpacity>
                </View>
              )}
            </Animated.View>
            
            <View style={styles.footer}>
              <Text style={styles.footerText}>
                Need help? <Text style={styles.footerLink}>Contact Support</Text>
              </Text>
            </View>
          </ScrollView>
        </KeyboardAvoidingView>
      </SafeAreaView>
    </View>
  );
}

const styles = StyleSheet.create({
  mainContainer: {
    flex: 1,
    backgroundColor: "#0F172A",
  },
  safeArea: {
    flex: 1,
  },
  blurCircle: {
    position: "absolute",
    borderRadius: 200,
    opacity: 0.4,
  },
  scrollContent: {
    flexGrow: 1,
    paddingHorizontal: 20,
    paddingBottom: 40,
  },
  header: {
    alignItems: "center",
    marginTop: height * 0.06,
    marginBottom: 32,
  },
  logoContainer: {
    position: "relative",
    marginBottom: 16,
  },
  logo: {
    width: 80,
    height: 80,
    zIndex: 2,
  },
  logoGlow: {
    position: "absolute",
    top: 5,
    left: 5,
    right: 5,
    bottom: 5,
    backgroundColor: "#3B82F6",
    borderRadius: 40,
    opacity: 0.2,
    transform: [{ scale: 1.3 }],
  },
  title: {
    fontSize: 32,
    fontWeight: "900",
    color: "#FFFFFF",
    letterSpacing: -1,
  },
  cityText: {
    color: "#3B82F6",
  },
  subtitle: {
    fontSize: 15,
    color: "#64748B",
    marginTop: 6,
    fontWeight: "500",
    textAlign: "center",
  },
  authCard: {
    backgroundColor: "rgba(30, 41, 59, 0.4)",
    borderRadius: 28,
    borderWidth: 1,
    borderColor: "rgba(255, 255, 255, 0.08)",
    padding: 24,
    shadowColor: "#000",
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.3,
    shadowRadius: 20,
    elevation: 5,
  },
  tabContainer: {
    flexDirection: "row",
    backgroundColor: "rgba(15, 23, 42, 0.6)",
    borderRadius: 14,
    padding: 4,
    marginBottom: 24,
    position: "relative",
  },
  tabSlider: {
    position: "absolute",
    top: 4,
    bottom: 4,
    left: 4,
    width: (width - 88 - 8) / 2,
    backgroundColor: "#3B82F6",
    borderRadius: 10,
  },
  tab: {
    flex: 1,
    paddingVertical: 10,
    alignItems: "center",
    zIndex: 1,
  },
  tabText: {
    fontSize: 14,
    fontWeight: "700",
    color: "#64748B",
  },
  tabTextActive: {
    color: "#FFFFFF",
  },
  form: {
    gap: 12,
  },
  forgotHeaderTitle: {
    fontSize: 24,
    fontWeight: "800",
    color: "#FFFFFF",
    marginBottom: 8,
  },
  forgotSubtitle: {
    fontSize: 14,
    color: "#64748B",
    lineHeight: 20,
    marginBottom: 12,
  },
  inputWrapper: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "rgba(15, 23, 42, 0.5)",
    borderRadius: 14,
    borderWidth: 1,
    borderColor: "rgba(255, 255, 255, 0.03)",
    paddingHorizontal: 16,
    height: 56,
  },
  inputWrapperFocused: {
    borderColor: "rgba(59, 130, 246, 0.5)",
    backgroundColor: "rgba(15, 23, 42, 0.8)",
  },
  input: {
    flex: 1,
    marginLeft: 12,
    fontSize: 15,
    color: "#FFFFFF",
    fontWeight: "500",
  },
  forgotBtn: {
    alignSelf: "flex-end",
    marginTop: -2,
  },
  forgotText: {
    color: "#3B82F6",
    fontSize: 13,
    fontWeight: "600",
  },
  mainBtnContainer: {
    marginTop: 8,
    borderRadius: 14,
    overflow: "hidden",
  },
  mainBtn: {
    height: 56,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
  },
  mainBtnText: {
    color: "#FFFFFF",
    fontSize: 16,
    fontWeight: "800",
    letterSpacing: 0.5,
  },
  backBtn: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    marginTop: 12,
    gap: 8,
  },
  backBtnText: {
    color: "#64748B",
    fontSize: 14,
    fontWeight: "600",
  },
  successContainer: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "rgba(16, 185, 129, 0.1)",
    padding: 12,
    borderRadius: 12,
    gap: 10,
    marginTop: 4,
  },
  successText: {
    flex: 1,
    color: "#10B981",
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 18,
  },
  divider: {
    flexDirection: "row",
    alignItems: "center",
    marginVertical: 20,
  },
  line: {
    flex: 1,
    height: 1,
    backgroundColor: "rgba(255, 255, 255, 0.05)",
  },
  dividerText: {
    marginHorizontal: 12,
    fontSize: 11,
    color: "#475569",
    fontWeight: "800",
    letterSpacing: 1.5,
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "center",
    gap: 12,
  },
  socialBtn: {
    width: 52,
    height: 52,
    borderRadius: 14,
    backgroundColor: "rgba(255, 255, 255, 0.03)",
    borderWidth: 1,
    borderColor: "rgba(255, 255, 255, 0.05)",
    alignItems: "center",
    justifyContent: "center",
  },
  footer: {
    marginTop: 24,
    alignItems: "center",
  },
  footerText: {
    color: "#475569",
    fontSize: 13,
    fontWeight: "500",
  },
  footerLink: {
    color: "#64748B",
    fontWeight: "700",
  },
});